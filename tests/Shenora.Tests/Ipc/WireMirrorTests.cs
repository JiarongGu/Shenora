using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shenora;
using Shenora.Windows;
using Shenora.Modules.Clipboard;
using Shenora.Modules.FileDialog;
using Shenora.Modules.Media;
using Shenora.Modules.Platform;
using Shenora.Modules.Requests;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The cross-language mirror TRIPWIRE (P5.5 H6). The C#⇄TS wire contract is asserted on both sides —
/// and that was the problem: each suite checked its OWN hand-written literals, so nothing compared the
/// two SETS. `SCOPE_REQUIRED` had existed in <see cref="IpcErrorCodes"/> and been emitted by
/// <c>ScopedContainerRouter</c> for two phases while being entirely absent from `types.ts`, so a scoped
/// app could not match it by constant — all while the docs claimed the mirror was name-for-name.
///
/// These tests read the TypeScript SOURCE rather than a generated artifact, on purpose: the source is
/// what an adopter imports, and a build step in between is one more place for the two to diverge.
/// </summary>
public class WireMirrorTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return dir!;
    }

    private static string ClientSource(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "Shenora.React", "src", fileName);
        Assert.True(File.Exists(path), $"client source not found: {path}");
        return StripBlockComments(File.ReadAllText(path));
    }

    /// <summary>
    /// Remove <c>/* … */</c> before parsing. Found the hard way while adding <c>ShellInfo</c>: the
    /// body matchers below are non-greedy up to the first <c>}</c>, and a JSDoc <c>{@link Foo}</c>
    /// inside an interface ENDS THE MATCH EARLY — so every field after it silently vanishes from the
    /// comparison.
    /// <para>
    /// <b>Measured, not assumed</b> (the stripper was made a no-op and the suite re-run):
    /// <see cref="ShellInfo_fields_match_the_host"/> fails with
    /// <c>Expected: ["name","capabilities"] / Actual: ["name"]</c>, and
    /// <see cref="Shell_capability_names_match_the_host"/> still PASSES — <see cref="ParseConstObject"/>
    /// is anchored on a trailing <c>as const;</c>, so the engine backtracks past the brace of
    /// <c>{@link}</c> on its own. <see cref="ParseInterfaceFieldNames"/> has no such anchor.
    /// </para>
    /// <para>
    /// So this bug is a FALSE ALARM, not a silent pass: it fails a correct mirror and blames the wrong
    /// side. That is worth fixing anyway, because of what the alarm invites — the natural response to a
    /// mirror test failing when you can see the mirror is fine is to loosen the test, and a subset check
    /// is exactly the tripwire-checking-nothing this file exists to prevent. Fix the parser, never the
    /// assertion. Line comments are left alone deliberately: stripping <c>//</c> would corrupt any
    /// <c>https://</c> inside a string literal.
    /// </para>
    /// </summary>
    private static string StripBlockComments(string source) =>
        Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>Every `name: 'VALUE',` entry inside a named `export const X = { … } as const;` block.</summary>
    private static Dictionary<string, string> ParseConstObject(string source, string exportName)
    {
        var block = Regex.Match(source,
            $@"export\s+const\s+{Regex.Escape(exportName)}\s*=\s*\{{(?<body>.*?)\}}\s*as\s+const\s*;",
            RegexOptions.Singleline);
        Assert.True(block.Success, $"could not find `export const {exportName} = {{ … }} as const;`");

        return Regex.Matches(block.Groups["body"].Value, @"(?<key>\w+)\s*:\s*'(?<value>[^']*)'")
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every top-level field name inside a named <c>export interface X { … }</c> block — a lighter
    /// parser than <see cref="ParseConstObject"/> because an interface names TYPES, not string
    /// literals, so only the key before each <c>:</c>/<c>?:</c> is comparable across languages.
    /// </summary>
    private static HashSet<string> ParseInterfaceFieldNames(string source, string exportName)
    {
        // `[^{]*` between the name and the brace, because everything a declaration can carry there is
        // optional and this pattern has now been too strict TWICE. First the `extends` clause, which the
        // file-dialog options needed (2026-08-05): `export interface OpenFileOptions extends
        // FileDialogOptions {` matched NOTHING. Then the TYPE PARAMETER — `export interface
        // IpcRequest<TPayload = unknown> {`, hit while pinning the envelopes, and the four biggest
        // shapes on this wire are all generic. Both times the failure said "could not find the
        // interface" about an interface that was right there.
        // 🔴 Fixed in the PARSER, per this file's own rule. The alternative reading ("the mirror is fine,
        // loosen the check") is how a tripwire stops checking anything — and it is the tempting one,
        // because the alarm is loud and the code under it is correct.
        var block = Regex.Match(source,
            $@"export\s+interface\s+{Regex.Escape(exportName)}\b[^{{]*\{{(?<body>.*?)\}}",
            RegexOptions.Singleline);
        Assert.True(block.Success, $"could not find `export interface {exportName} {{ … }}`");

        return Regex.Matches(block.Groups["body"].Value, @"(?<key>\w+)\s*\??\s*:")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] ParseStringArray(string source, string exportName)
    {
        var block = Regex.Match(source,
            $@"export\s+const\s+{Regex.Escape(exportName)}[^=]*=\s*\[(?<body>.*?)\]\s*;",
            RegexOptions.Singleline);
        Assert.True(block.Success, $"could not find `export const {exportName} … = [ … ];`");
        // Entries reference IpcErrorCodes.x rather than repeating the literal, so resolve through the map.
        var codes = ParseConstObject(source, "IpcErrorCodes");
        return Regex.Matches(block.Groups["body"].Value, @"IpcErrorCodes\.(?<key>\w+)")
            .Select(m => codes[m.Groups["key"].Value])
            .ToArray();
    }

    [Fact]
    public void Every_host_error_code_exists_on_the_client_and_vice_versa()
    {
        var hostCodes = typeof(IpcErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var source = ClientSource("types.ts");
        var clientCodes = ParseConstObject(source, "IpcErrorCodes").Values.ToHashSet(StringComparer.Ordinal);
        var clientOnly = ParseStringArray(source, "ClientOnlyIpcErrorCodes").ToHashSet(StringComparer.Ordinal);

        // Sanity-check the parser itself before trusting its verdict — a regex that silently matched
        // nothing would make this test pass for the wrong reason.
        Assert.NotEmpty(hostCodes);
        Assert.NotEmpty(clientCodes);
        Assert.NotEmpty(clientOnly);

        var missingOnClient = hostCodes.Except(clientCodes).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missingOnClient.Length == 0,
            $"The host emits these codes but the client cannot name them: {string.Join(", ", missingOnClient)}. " +
            "Add them to IpcErrorCodes in src/Shenora.React/src/types.ts.");

        // The other direction, minus the deliberately client-only ones (which the client DECLARES, so
        // this stays honest instead of carrying a second hard-coded list here).
        var extraOnClient = clientCodes.Except(hostCodes).Except(clientOnly).Order(StringComparer.Ordinal).ToArray();
        Assert.True(extraOnClient.Length == 0,
            $"The client names these codes but the host never emits them: {string.Join(", ", extraOnClient)}. " +
            "Either add them to Shenora.Core.Ipc.IpcErrorCodes or list them in ClientOnlyIpcErrorCodes.");
    }

    [Fact]
    public void The_handshake_route_matches_on_both_sides()
    {
        var source = ClientSource("types.ts");

        Assert.Equal(WebViewIpcBridge.HandshakeModule, ParseExportedString(source, "HANDSHAKE_MODULE"));
        Assert.Equal(WebViewIpcBridge.HandshakeType, ParseExportedString(source, "HANDSHAKE_TYPE"));
    }

    [Fact]
    public void The_envelope_categories_match_on_both_sides()
    {
        // The category is what routes a host message to "resolve a pending call" vs "unbundle a
        // notification batch"; a one-sided rename silently breaks delivery in one direction only.
        var categories = ParseConstObject(ClientSource("types.ts"), "IpcCategories");
        Assert.Equal(IpcCategories.Ipc, categories["ipc"]);
        Assert.Equal(IpcCategories.Notification, categories["notification"]);
        Assert.Equal(2, categories.Count); // a new category on either side must be a deliberate change
    }

    private static string ParseExportedString(string source, string name)
    {
        var match = Regex.Match(source, $@"export\s+const\s+{Regex.Escape(name)}\s*=\s*'(?<value>[^']*)'");
        Assert.True(match.Success, $"could not find `export const {name} = '…'`");
        return match.Groups["value"].Value;
    }

    /// <summary>
    /// <see cref="IpcRequestState"/> (design §4.6/§9.1) crosses the wire as its camelCase name for
    /// free — <see cref="IpcJson"/> installs a camelCase <c>JsonStringEnumConverter</c> — so the
    /// client's <c>OperationStatuses</c> const object must name exactly the same set of strings.
    /// A status added on one side and not the other must fail THIS test by name, not pass a green
    /// suite that never compared the two sets (the same disease <c>SCOPE_REQUIRED</c> had).
    /// </summary>
    [Fact]
    public void Every_request_state_exists_on_both_sides()
    {
        var host = Enum.GetNames<IpcRequestState>()
            .Select(n => JsonNamingPolicy.CamelCase.ConvertName(n))
            .ToHashSet(StringComparer.Ordinal);
        var client = ParseConstObject(ClientSource("requests.ts"), "IpcRequestStates").Values.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(host);      // parser self-check: a regex that matched nothing must not pass
        Assert.NotEmpty(client);
        Assert.Equal(host, client);
    }

    /// <summary>
    /// THIS TEST IS THE MIRROR ITSELF, and it exists because the client used to hardcode its request
    /// event names with nothing comparing them against <see cref="IpcRequestEvents"/> — so a host rename
    /// left the suite green and the client deaf, the exact disease
    /// <see cref="Every_host_error_code_exists_on_the_client_and_vice_versa"/> already catches for error
    /// codes. Both sides are read from source here, so a rename on either half fails BY NAME.
    /// </summary>
    [Fact]
    public void Request_event_names_match_the_host()
    {
        var client = ParseConstObject(ClientSource("requests.ts"), "IpcRequestEventTypes");

        Assert.NotEmpty(client);   // parser self-check: a regex that matched nothing must not pass
        Assert.Equal(IpcRequestEvents.Updated, client["Updated"]);
        Assert.Equal(IpcRequestEvents.Removed, client["Removed"]);
        // RESUME_REQUESTED / WAIT_REQUESTED went with the waiting band (D66). The client must not keep
        // naming them either, which the ONE-WAY check below enforces: an extra client key is a client
        // still speaking a language the host retired.
        Assert.Equal(2, client.Count);
    }

    [Fact]
    public void Request_route_names_match_the_hosts_module()
    {
        var client = ParseConstObject(ClientSource("requests.ts"), "IpcRequestRoutes");

        Assert.NotEmpty(client);   // parser self-check
        Assert.Equal(IpcRequestsModule.ListType, client["List"]);
        Assert.Equal(IpcRequestsModule.CancelType, client["Cancel"]);
        Assert.Equal(IpcRequestsModule.ClearFinishedType, client["ClearFinished"]);
        // THREE routes, not six. RESUME/WAIT/DISMISS went with the waiting band (D66), and the count
        // assertion is what makes their removal enforceable rather than merely intended — a client
        // still shipping them would otherwise pass this test by simply not being asked about them.
        Assert.Equal(3, client.Count);
    }

    [Fact]
    public void The_default_requests_module_name_matches_the_host()
    {
        var source = ClientSource("requests.ts");

        Assert.Equal(new IpcRequestTrackerOptions().ModuleName, ParseExportedString(source, "IpcRequestsModuleName"));
    }

    /// <summary>
    /// <see cref="IpcProgress"/> replaced a bare 0–100 <c>int?</c> (generic-library audit, before
    /// publish) — a NEW wire shape both sides name, so it needs its own tripwire. This compares the
    /// SET of field names (camelCased) rather than trusting the two sides to stay in step by
    /// inspection — the exact disease this whole file exists to catch for everything else on this wire.
    /// </summary>
    [Fact]
    public void IpcProgress_fields_match_the_host()
    {
        AssertMirroredFields(typeof(IpcProgress), "requests.ts", "IpcProgress");
    }

    /// <summary>
    /// <b><see cref="IpcRequestStatus"/> is the biggest shape on this wire and had NO mirror at all until
    /// the 0.2.0 design pass</b> — it is both the entire <c>OPERATION_UPDATED</c> payload and the
    /// element type of the <c>LIST</c> response, so a field present on one side and not the other is a
    /// silent hole in every operation-driven UI.
    /// <para>
    /// It was missed because of a plausible-sounding claim, which is why this comment records it:
    /// <see cref="IpcProgress_fields_match_the_host"/>'s own doc used to assert that
    /// "<c>IpcRequestStatus</c>'s other fields are pinned by <c>[JsonPropertyName]</c> + the API baseline
    /// on the host side". Both halves are true and together they still prove nothing about the MIRROR —
    /// they pin the HOST's names against the HOST's own baseline, and no test compared them to the TS
    /// interface. The smaller, newer type got a tripwire; the one that actually carries the payload did
    /// not. (Found when a whole-codebase review removed <c>ResumePayload</c> from both sides by hand
    /// and nothing verified that both hands had moved.)
    /// </para>
    /// </summary>
    [Fact]
    public void IpcRequestStatus_fields_match_the_host()
    {
        AssertMirroredFields(typeof(IpcRequestStatus), "requests.ts", "IpcRequestStatus");
    }

    /// <summary>
    /// <see cref="ShellInfo"/> is the handshake's response data and the thing a page branches its
    /// LAYOUT on, so a one-sided rename does not fail loudly — it renders the wrong tree. Pinned like
    /// every other shape on this wire.
    /// </summary>
    [Fact]
    public void ShellInfo_fields_match_the_host()
    {
        var host = typeof(ShellInfo).GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);
        var client = ParseInterfaceFieldNames(ClientSource("types.ts"), "ShellInfo");

        Assert.NotEmpty(host);      // parser/reflection self-check
        Assert.NotEmpty(client);
        Assert.Equal(host, client);
    }

    /// <summary>
    /// The well-known capability NAMES. These are compared by VALUE, not by member name: the client
    /// keys are lowerCamel while the host constants are PascalCase, and it is the string on the wire
    /// that a page tests against. A host renaming one silently stops matching the client's check and
    /// the feature just disappears from the UI.
    /// </summary>
    [Fact]
    public void Shell_capability_names_match_the_host()
    {
        var client = ParseConstObject(ClientSource("types.ts"), "ShellCapabilities");

        Assert.NotEmpty(client);    // parser self-check
        Assert.Equal(ShellCapability.WindowChrome, client["windowChrome"]);
        Assert.Equal(ShellCapability.DropZones, client["dropZones"]);
        Assert.Equal(ShellCapability.FilePicker, client["filePicker"]);
        Assert.Equal(ShellCapability.FolderPicker, client["folderPicker"]);
        Assert.Equal(ShellCapability.SavePicker, client["savePicker"]);
        Assert.Equal(ShellCapability.SecondaryWindows, client["secondaryWindows"]);
        Assert.Equal(ShellCapability.Tray, client["tray"]);

        // The host must not grow one the client cannot name — that is the SCOPE_REQUIRED disease
        // this whole file exists to catch.
        var hostNames = typeof(ShellCapability).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(hostNames, client.Values.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The file-dialog MODULE and ROUTE strings, read from the client's actual <c>send()</c> calls rather
    /// than from its request-map interface.
    /// <para>
    /// Deliberately the call sites: <c>FileDialogRequests</c> is not exported (the same shape
    /// <c>windowCommands.ts</c> uses), and more importantly a route the map DECLARES but the methods never
    /// send would still mirror perfectly while doing nothing. What a client actually puts on the wire is
    /// the thing worth pinning.
    /// </para>
    /// </summary>
    [Fact]
    public void File_dialog_module_and_routes_match_the_host()
    {
        var source = ClientSource("fileDialogs.ts");

        // ⚠ The dot is part of the character class because the kit's own modules carry the RESERVED
        // `SHENORA.` prefix (D64). Without it this pattern silently stopped matching when the names moved
        // — which the gate reported honestly as "could not find the call", not as a mismatch. A wire
        // tripwire whose PARSER can fail to find its subject needs that failure to be loud, which is why
        // the Assert.True below exists and why it is worth keeping.
        var module = Regex.Match(source, @"super\('(?<module>[A-Z_.]+)'");
        Assert.True(module.Success, "could not find the FileDialogs `super('MODULE'` call");
        Assert.Equal(FileDialogModule.Module, module.Groups["module"].Value);

        // ⚠ The type argument is OPTIONAL and must stay so: a call site that names the response defeats
        // the payload check (`moduleService.test.ts` pins that), so the shipped form has no `<…>`.
        // This pattern demanded one and stopped matching the moment they were removed — caught by
        // the Assert.NotEmpty self-check below, which is the reason it is there.
        var routes = Regex.Matches(source, @"\.send(?:<[^>]*>)?\('(?<route>[A-Z_]+)'")
            .Select(m => m.Groups["route"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(routes);    // parser self-check

        var hostRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            FileDialogModule.OpenFileType,
            FileDialogModule.OpenFolderType,
            FileDialogModule.SaveFileType,
            FileDialogModule.SaveTextType,
        };
        Assert.Equal(hostRoutes, routes);
    }

    /// <summary>
    /// The client's TYPE pin lists every type its barrel exports.
    /// <para>
    /// 🔴 <b>The pin is a hand-written tuple, so it drifts silently — and did, by fourteen types.</b> A
    /// runtime export check cannot help: a type has no runtime binding, which is the whole reason the
    /// tuple exists. But nothing checked the TUPLE itself, so every type added after it was written was
    /// simply absent, and deleting any of them from <c>index.ts</c> would break consumers while both
    /// runtime assertions stayed green — exactly the hole the pin was written for in the first place.
    /// </para>
    /// <para>
    /// Comparing the two SETS is the fix, and it belongs here for the same reason the <c>send</c> check
    /// does: the React package's tsconfig has no node types, so a test there cannot read its own sources.
    /// </para>
    /// </summary>
    [Fact]
    public void The_type_pin_lists_every_type_the_barrel_exports()
    {
        var barrel = ClientSource("index.ts");
        var pinSource = ClientSource("index.test.ts");

        // `export type { A, B } from '…'` — the whole-block form.
        var exported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(barrel, @"export\s+type\s*\{([^}]*)\}"))
            foreach (var name in block.Groups[1].Value.Split(','))
                if (name.Trim().Length > 0) exported.Add(name.Trim());

        // `export { thing, type X } from '…'` — a type riding along in a value export.
        foreach (Match block in Regex.Matches(barrel, @"export\s*\{([^}]*)\}"))
            foreach (var part in block.Groups[1].Value.Split(','))
                if (part.Trim().StartsWith("type ", StringComparison.Ordinal))
                    exported.Add(part.Trim()[5..].Trim());

        var tuple = Regex.Match(pinSource, @"type ExportedTypeSurface = \[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(tuple.Success, "could not find `type ExportedTypeSurface = [ … ];` in index.test.ts");
        var pinned = Regex.Matches(tuple.Groups["body"].Value, @"([A-Z][A-Za-z0-9_]*)")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        // Parser self-checks — a regex that matched nothing must not pass for the wrong reason.
        Assert.True(exported.Count > 0, "parsed NO type exports out of index.ts");
        Assert.True(pinned.Count > 0,
            $"parsed NO names out of ExportedTypeSurface (body length {tuple.Groups["body"].Value.Length})");

        var unpinned = exported.Except(pinned).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unpinned.Length == 0,
            "index.ts exports these types and ExportedTypeSurface does not list them, so deleting one "
            + "would break consumers with every runtime assertion still green: "
            + string.Join(", ", unpinned));
    }

    /// <summary>
    /// No shipped client names the RESPONSE type argument on <c>send</c>, because naming it silently
    /// turns the payload check off.
    /// <para>
    /// 🔴 <b>A source check, because the broken form COMPILES — that is the whole defect, so no
    /// type-level pin can catch it.</b> TypeScript has no partial type-argument inference:
    /// <c>send&lt;Response&gt;('ROUTE', …)</c> makes the route parameter fall back to its default (the
    /// union of every key), so <c>payload</c> widens to the union of every route's payload. Verified
    /// with <c>tsc</c>: an identical wrong payload is a TS2353 without the type argument and clean with
    /// it. The response is inferred from the method's declared return type instead.
    /// </para>
    /// <para>
    /// ⚠ <b>Every shipped call site used the broken form</b> — four in <c>fileDialogs.ts</c>, three in
    /// <c>clipboard.ts</c>, one in <c>windowCommands.ts</c> — so the feature checked nothing anywhere it
    /// was actually used, while its own <c>@ts-expect-error</c> pin passed because that pin uses the
    /// inferred form. It lives HERE rather than in vitest because the React package's tsconfig has no
    /// node types, so a test there cannot read its own sources.
    /// </para>
    /// </summary>
    [Fact]
    public void No_client_names_the_response_type_argument_on_send()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        var clientDir = Path.Combine(dir!, "src", "Shenora.React", "src");

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(clientDir, "*.ts")
                     .Where(f => !f.EndsWith(".test.ts", StringComparison.Ordinal)))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // `.send<` is a CALL naming the response. The declaration in moduleService.ts reads
                // `send<TResponse = unknown, …>(` with no leading dot, so it is not matched.
                if (lines[i].Contains(".send<", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
            }
        }

        Assert.True(scanned > 5, $"parser self-check: only {scanned} client source(s) scanned");
        Assert.True(offenders.Count == 0,
            "These call sites name the response type argument, which disables the payload check. Declare "
            + "the method's return type instead:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The back-gesture MODULE, ROUTES and the EVENT type — same shape as the pins above, plus the event
    /// name, which the others do not have to carry.
    /// <para>
    /// 🔴 <b>The event is the half that fails silently and the half only this test watches.</b> A route
    /// that drifts produces a rejected request the page can see; a drifted EVENT name produces a
    /// subscription that simply never fires, which is indistinguishable from a user who never pressed
    /// back — and the consequence is the app quitting under them, on a device, with nothing logged.
    /// </para>
    /// </summary>
    [Fact]
    public void Back_navigation_module_routes_and_event_match_the_host()
    {
        var source = ClientSource("backNavigation.ts");

        var module = Regex.Match(source, @"BACK_MODULE\s*=\s*'(?<module>[A-Z_.]+)'");
        Assert.True(module.Success, "could not find the client's `BACK_MODULE = '…'` declaration");
        Assert.Equal(BackNavigation.Module, module.Groups["module"].Value);

        // The facade answers on the same name a press is published under; the client has ONE constant for
        // both, so a host that ever split them would go unnoticed here.
        Assert.Equal(BackNavigationModule.Module, BackNavigation.Module);

        var pressed = Regex.Match(source, @"BACK_PRESSED\s*=\s*'(?<type>[A-Z_]+)'");
        Assert.True(pressed.Success, "could not find the client's `BACK_PRESSED = '…'` declaration");
        Assert.Equal(BackNavigation.PressedType, pressed.Groups["type"].Value);

        var routes = Regex.Matches(source, @"\.send(?:<[^>]*>)?\('(?<route>[A-Z_]+)'")
            .Select(m => m.Groups["route"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(routes);    // parser self-check

        var hostRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            BackNavigation.InterceptType,
            BackNavigation.ResolveType,
        };
        Assert.Equal(hostRoutes, routes);
    }

    /// <summary>
    /// The lifecycle MODULE and its two EVENT types. No routes at all here — the page only listens —
    /// so the events are the entire contract, and a drifted one is a subscription that never fires.
    /// </summary>
    [Fact]
    public void App_lifecycle_module_and_events_match_the_host()
    {
        var source = ClientSource("appLifecycle.ts");

        var module = Regex.Match(source, @"LIFECYCLE_MODULE\s*=\s*'(?<module>[A-Z_.]+)'");
        Assert.True(module.Success, "could not find the client's `LIFECYCLE_MODULE = '…'` declaration");
        Assert.Equal(AppLifecycle.Module, module.Groups["module"].Value);

        var stopped = Regex.Match(source, @"LIFECYCLE_STOPPED\s*=\s*'(?<type>[A-Z_]+)'");
        Assert.True(stopped.Success, "could not find the client's `LIFECYCLE_STOPPED = '…'` declaration");
        Assert.Equal(AppLifecycle.StoppedType, stopped.Groups["type"].Value);

        var resumed = Regex.Match(source, @"LIFECYCLE_RESUMED\s*=\s*'(?<type>[A-Z_]+)'");
        Assert.True(resumed.Success, "could not find the client's `LIFECYCLE_RESUMED = '…'` declaration");
        Assert.Equal(AppLifecycle.ResumedType, resumed.Groups["type"].Value);
    }

    /// <summary>
    /// The resume payload — one field, and the one the whole type exists to carry. A drifted NAME here
    /// reads as "no duration reported", which the client maps to null, which a page reads as a first
    /// launch: a silent, plausible wrong answer on every resume.
    /// </summary>
    [Fact]
    public void App_lifecycle_report_mirrors_the_client()
    {
        AssertMirroredFields(typeof(AppLifecycleReport), "appLifecycle.ts", "AppLifecycleReport");
    }

    /// <summary>
    /// The REQUEST payload keys — <c>enabled</c>, <c>token</c>, <c>handled</c> — which are hand-typed
    /// literals on both sides and were the one half of this wire nothing watched.
    /// <para>
    /// 🔴 A drift here does not fail loudly: the host answers <c>MISSING_PAYLOAD_VALUE</c> and the
    /// client's own housekeeping catch turns it into a warning, so interception is never established
    /// while the page still believes it holds the back gesture — i.e. the app quits from every screen.
    /// </para>
    /// </summary>
    [Fact]
    public void Back_navigation_REQUEST_payload_keys_match_the_host()
    {
        var source = ClientSource("backNavigation.ts");

        // The client declares them once, in its request map. Read them from there rather than from the
        // call sites, which is where they would drift.
        var requests = Regex.Match(source, @"interface\s+BackRequests\s*\{(?<body>.*?)\}\s*\n",
            RegexOptions.Singleline);
        Assert.True(requests.Success, "could not find `interface BackRequests { … }`");

        var keys = Regex.Matches(requests.Groups["body"].Value, @"\{(?<fields>[^}]*)\}")
            .SelectMany(m => Regex.Matches(m.Groups["fields"].Value, @"(?<key>\w+)\s*:")
                .Select(f => f.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);   // parser self-check

        // The host reads exactly these three through PayloadHelper, and there is no third spelling.
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "enabled", "token", "handled" }, keys);

        var module = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shenora", "Modules", "Platform",
            "BackNavigationModule.cs"));
        foreach (var key in keys)
            Assert.Contains($"\"{key}\"", module, StringComparison.Ordinal);
    }

    /// <summary>
    /// The back press PAYLOAD, which is one field and entirely load-bearing: the token is what stops an
    /// answer to a timed-out press being applied to the press after it.
    /// </summary>
    [Fact]
    public void Back_navigation_payloads_mirror_the_client()
    {
        AssertMirroredFields(typeof(BackNavigationEvent), "backNavigation.ts", "BackNavigationEvent");
        AssertMirroredFields(typeof(BackNavigationResult), "backNavigation.ts", "BackNavigationResult");
    }

    /// <summary>
    /// The clipboard MODULE and ROUTE strings, read from the client's actual <c>send()</c> calls — same
    /// shape and same reasoning as the file-dialog pin above.
    /// </summary>
    [Fact]
    public void Clipboard_module_and_routes_match_the_host()
    {
        var source = ClientSource("clipboard.ts");

        var module = Regex.Match(source, @"super\('(?<module>[A-Z_.]+)'");
        Assert.True(module.Success, "could not find the Clipboard `super('MODULE'` call");
        Assert.Equal(ClipboardModule.Module, module.Groups["module"].Value);

        // ⚠ The type argument is OPTIONAL and must stay so: a call site that names the response defeats
        // the payload check (`moduleService.test.ts` pins that), so the shipped form has no `<…>`.
        // This pattern demanded one and stopped matching the moment they were removed — caught by
        // the Assert.NotEmpty self-check below, which is the reason it is there.
        var routes = Regex.Matches(source, @"\.send(?:<[^>]*>)?\('(?<route>[A-Z_]+)'")
            .Select(m => m.Groups["route"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(routes);    // parser self-check

        var hostRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            ClipboardModule.ReadType,
            ClipboardModule.WriteType,
            ClipboardModule.ClearType,
        };
        Assert.Equal(hostRoutes, routes);
    }

    /// <summary>
    /// The clipboard's own media-type constants. Both sides hand these to the OTHER as dictionary keys, so
    /// a drifted pair does not fail — the host simply files the bytes under a name the page never asks for
    /// and the paste is empty, which is the silent shape this whole file exists to catch.
    /// </summary>
    [Fact]
    public void Clipboard_media_type_constants_match_the_host()
    {
        var source = ClientSource("clipboard.ts");

        string Client(string name)
        {
            var match = Regex.Match(source, $@"export const {name} = '(?<value>[^']+)'");
            Assert.True(match.Success, $"could not find the client's `{name}` constant");
            return match.Groups["value"].Value;
        }

        Assert.Equal(ClipboardContent.PngImage, Client("PNG_IMAGE"));
        Assert.Equal(ClipboardContent.Html, Client("HTML"));
    }

    /// <summary>
    /// The three per-method options shapes and the result. These are the reason the options were split at
    /// all: the page NAMES them now, so a host-side field the client cannot express is a capability an
    /// adopter simply cannot reach, and a client field the host never reads is silently ignored input.
    /// </summary>
    [Fact]
    public void File_dialog_option_and_result_shapes_match_the_host()
    {
        const string file = "fileDialogs.ts";
        // Each derived type is compared against its own interface PLUS the base it extends — C# reflection
        // already includes inherited properties, the TS parser reads one declaration.
        AssertMirroredFields(typeof(OpenFileOptions), file, "OpenFileOptions", "FileDialogOptions");
        AssertMirroredFields(typeof(OpenFolderOptions), file, "OpenFolderOptions", "FileDialogOptions");
        AssertMirroredFields(typeof(SaveFileOptions), file, "SaveFileOptions", "FileDialogOptions");
        AssertMirroredFields(typeof(FileDialogResult), file, "FileDialogResult");
        AssertMirroredFields(typeof(FileDialogFilter), file, "FileDialogFilter");
    }

    /// <summary>
    /// The shared mirror check: a host record's property names, camelCased the way
    /// <see cref="IpcJson"/> serializes them, must equal the TS interface's field names exactly.
    /// One helper so a third wire shape cannot be added with a subtly weaker check.
    /// </summary>
    /// <param name="hostType">The host record. Inherited properties count — they are on the wire too.</param>
    /// <param name="sourceFile">
    /// Which client module declares it. Explicit rather than defaulted to <c>requests.ts</c>: a default
    /// here is how a later shape gets pointed at the wrong file and mirrors nothing, which is the exact
    /// "tripwire checking nothing" this file exists to prevent.
    /// </param>
    /// <param name="clientInterface">The exported TS interface.</param>
    /// <param name="clientBaseInterfaces">
    /// Any interfaces <paramref name="clientInterface"/> <c>extends</c>. TypeScript inheritance is
    /// declaration-by-declaration and the parser reads ONE block, while C# reflection already returns
    /// inherited properties — so without this the two sides are compared at different depths and a base
    /// field reads as "missing on the client" when it is right there.
    /// </param>
    private static void AssertMirroredFields(Type hostType, string sourceFile, string clientInterface,
        params string[] clientBaseInterfaces)
    {
        var host = hostType.GetProperties()
            .Where(OnTheWire)
            .Select(WireName)
            .ToHashSet(StringComparer.Ordinal);
        var source = ClientSource(sourceFile);
        var client = ParseInterfaceFieldNames(source, clientInterface);
        foreach (var baseInterface in clientBaseInterfaces)
            client.UnionWith(ParseInterfaceFieldNames(source, baseInterface));

        Assert.NotEmpty(host);      // parser/reflection self-check: neither side may be silently empty
        Assert.NotEmpty(client);

        var missingOnClient = host.Except(client).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missingOnClient.Length == 0,
            $"The host's {hostType.Name} carries these fields but the client's `{clientInterface}` does not name " +
            $"them: {string.Join(", ", missingOnClient)}. A consumer cannot read what it cannot name.");

        var extraOnClient = client.Except(host).Order(StringComparer.Ordinal).ToArray();
        Assert.True(extraOnClient.Length == 0,
            $"The client's `{clientInterface}` names these fields but the host's {hostType.Name} never sends " +
            $"them: {string.Join(", ", extraOnClient)}. They will always be undefined at runtime.");
    }

    /// <summary>
    /// Does this property actually go on the wire? <c>[JsonIgnore]</c> in its default (Always) condition
    /// says no — <see cref="IpcNotification.CoalesceKey"/> is the case that forced this, a host-side
    /// buffering hint whose own doc says it is "deliberately absent from the TS mirror". Without the
    /// check the envelope pin below would demand the client name a field the host never sends.
    /// <para>
    /// ⚠ Only <see cref="JsonIgnoreCondition.Always"/> counts. <c>WhenWritingNull</c> still ships the
    /// field whenever it has a value, so a client that cannot name it still has a hole.
    /// </para>
    /// </summary>
    private static bool OnTheWire(System.Reflection.PropertyInfo property) =>
        property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always };

    /// <summary>
    /// The name this property serializes under: <c>[JsonPropertyName]</c> wins, else the camelCase
    /// policy <see cref="IpcJson"/> applies. Reading the attribute matters because it is the ONE thing
    /// that can move a wire name without touching the C# name — the exact drift a mirror exists to catch,
    /// and the check would otherwise compare the client against a name nothing sends.
    /// </summary>
    private static string WireName(System.Reflection.PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    /// <summary>
    /// The four ENVELOPE shapes — the outermost thing on this wire and, until now, the only part of it
    /// with no mirror at all. Every module's payload was pinned while the container carrying them was
    /// not: renaming <c>IpcResponse.Success</c> on one side leaves every request pending forever, and
    /// dropping <c>scope</c> from the notification silently delivers a scoped event to every subscriber.
    /// <para>
    /// <c>EventMessage</c> is deliberately NOT here. The host's carries id/timestamp that never cross the
    /// wire (its unbundled client twin says so in its own doc), so it is not one shape in two languages
    /// and a mirror would be asserting a false equivalence.
    /// </para>
    /// </summary>
    [Fact]
    public void Envelope_fields_match_on_both_sides()
    {
        AssertMirroredFields(typeof(IpcRequest), "types.ts", "IpcRequest");
        AssertMirroredFields(typeof(IpcResponse), "types.ts", "IpcResponse");
        AssertMirroredFields(typeof(IpcNotification), "types.ts", "IpcNotification");
        AssertMirroredFields(typeof(IpcNotificationBatch), "types.ts", "IpcNotificationBatch");
        AssertMirroredFields(typeof(IpcError), "types.ts", "IpcError");
    }

    /// <summary>
    /// Window chrome's MODULE and ROUTES. Same shape as the file-dialog pin, and the same reason it is
    /// read from the client's <c>send()</c> calls: what the page actually puts on the wire.
    /// <para>
    /// ⚠ This one is worth having twice over, because its failure is INVISIBLE rather than loud: a
    /// frameless window whose <c>START_DRAG</c> route drifted still renders perfectly and simply stops
    /// being draggable. The module name has already moved once — D64's reserved prefix turned
    /// <c>WINDOW</c> into <c>SHENORA.WINDOW</c> — and both client and host docs kept saying <c>WINDOW</c>
    /// for releases afterwards, with nothing able to notice.
    /// </para>
    /// </summary>
    [Fact]
    public void Window_command_module_and_routes_match_the_host()
    {
        var source = ClientSource("windowCommands.ts");

        var module = Regex.Match(source, @"super\('(?<module>[A-Z_.]+)'");
        Assert.True(module.Success, "could not find the WindowCommands `super('MODULE'` call");
        Assert.Equal(WindowCommandModule.Module, module.Groups["module"].Value);

        var routes = Regex.Matches(source, @"\.send(?:<[^>]*>)?\('(?<route>[A-Z_]+)'")
            .Select(m => m.Groups["route"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(routes);    // parser self-check

        var hostRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            WindowCommandModule.MinimizeType,
            WindowCommandModule.ToggleMaximizeType,
            WindowCommandModule.CloseType,
            WindowCommandModule.IsMaximizedType,
            WindowCommandModule.StartDragType,
            WindowCommandModule.StartResizeType,
            WindowCommandModule.SetThemeType,
            WindowCommandModule.SetCaptionButtonsType,
        };
        Assert.Equal(hostRoutes, routes);
    }

    /// <summary>
    /// Drop zones, which are the one mechanism here whose whole VALUE is the wire: a page cannot learn a
    /// dropped file's path any other way, so a drifted <c>FILE_DROP</c> name is the feature disappearing.
    /// Both directions are pinned — the ROUTES the hook invokes and the EVENTS it subscribes to — because
    /// they drift independently and the event half has no request/response to fail.
    /// </summary>
    [Fact]
    public void Drop_zone_module_routes_and_events_match_the_host()
    {
        var source = ClientSource("useDropZone.ts");

        Assert.Equal(DropZoneManager.Module, ParseExportedString(source, "DROP_ZONE_MODULE"));

        // The hook has no BaseModuleService — it calls the bridge directly — so the routes are read from
        // `invoke(MODULE, 'ROUTE'` and the events from `subscribe<…>(MODULE, 'TYPE'`. Keeping them apart
        // is the point: a route pinned as an event would pass while the wrong half drifted.
        var routes = Regex.Matches(source, @"\.invoke\(DROP_ZONE_MODULE,\s*'(?<route>[A-Z_]+)'")
            .Select(m => m.Groups["route"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var events = Regex.Matches(source, @"\.subscribe(?:<[^>]*>)?\(DROP_ZONE_MODULE,\s*'(?<type>[A-Z_]+)'")
            .Select(m => m.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(routes);    // parser self-checks
        Assert.NotEmpty(events);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                DropZoneModule.RegisterType,
                DropZoneModule.UpdateType,
                DropZoneModule.UnregisterType,
                DropZoneModule.ShowType,
            },
            routes);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                DropZoneManager.DragEnterEvent,
                DropZoneManager.DragLeaveEvent,
                DropZoneManager.FileDropEvent,
            },
            events);
    }

    /// <summary>
    /// The media player's command vocabulary, which <c>mediaPlayer.ts</c> itself calls "a wire contract …
    /// the two halves agree by string or not at all" — a sentence nothing enforced until this test.
    /// <para>
    /// ⚠ The COMMANDS are compared as equal SETS, in both directions: the host emitting a command the page
    /// cannot name is a control that silently does nothing, and the page listening for one the host never
    /// sends is dead code that reads as support. <c>PLAYER_STATUS</c> is host-answered only (the page asks,
    /// it never listens), so it is not in the command set and is asserted separately.
    /// </para>
    /// </summary>
    [Fact]
    public void Media_player_module_commands_and_report_match_the_host()
    {
        var source = ClientSource("mediaPlayer.ts");

        // The two required members are irrelevant here and are given throwaway values: what is being
        // pinned is the DEFAULT module name, which is what the client hard-codes (an app that overrides
        // `Module` passes its own to the page too). Same shape as the requests-module check above.
        var access = new MediaAccessOptions { Resolve = _ => null, CacheRoot = "." };
        Assert.Equal(access.Module, ParseExportedString(source, "MEDIA_PLAYER_MODULE"));
        Assert.Equal(MediaPlayerModule.ReportType, ParseExportedString(source, "MEDIA_PLAYER_REPORT"));

        var client = ParseConstObject(source, "MediaPlayerCommands").Values.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(client);    // parser self-check

        var host = typeof(MediaPlayerEvents)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(host);

        Assert.Equal(host, client);
    }

    /// <summary>
    /// 🔴 THE COMPLETENESS CHECK — every family ABOVE is hand-written, so until this existed a wire
    /// vocabulary with no mirror was simply INVISIBLE. That is the allow-list shape this repo keeps
    /// paying for (`wire-reference`'s SECTIONS had it, and nine types were silently unpublished);
    /// found here 2026-08-18 with `MediaConversionEvents`/`MediaConversionErrorCodes` — named by the
    /// host, promised to the page by the CHANGELOG ("wait on READY, branch on FAILED's reason") and
    /// absent from the client entirely, so every page typed the raw strings.
    /// <para>
    /// It reads <c>docs/reference/wire.md</c>, which is GENERATED from the source constants and gated
    /// to match them — so a new wire type reaches this test the moment it exists, and a family that is
    /// genuinely host-only has to say so in <see cref="HostOnlyFamilies"/> rather than defaulting to
    /// unmirrored.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_wire_family_is_mirrored_on_the_client_or_declared_host_only()
    {
        var wire = File.ReadAllText(RepoFile("docs", "reference", "wire.md"));
        var values = Regex.Matches(wire, @"^\|\s`(?<type>\w+)\.(?<member>\w+)`\s*\|\s*`(?<value>[^`]*)`",
                                   RegexOptions.Multiline)
            .GroupBy(m => m.Groups["type"].Value,
                     m => m.Groups["value"].Value,
                     StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        Assert.NotEmpty(values);   // parser self-check: wire.md's row shape changed if this trips

        var clientSource = string.Concat(
            Directory.EnumerateFiles(RepoFile("src", "Shenora.React", "src"), "*.ts")
                .Where(f => !f.EndsWith(".test.ts", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        var unmirrored = values
            .Where(kv => !HostOnlyFamilies.Contains(kv.Key))
            // A family counts as mirrored when EVERY value it publishes is spelled on the client — a
            // partial mirror is how `PLAYER_STATUS` and two conversion events went missing inside
            // families that looked covered.
            .Where(kv => kv.Value.Any(v => v.Length > 0 && !clientSource.Contains($"'{v}'", StringComparison.Ordinal)))
            .Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value.Where(v => !clientSource.Contains($"'{v}'", StringComparison.Ordinal)))})")
            .ToArray();

        Assert.True(unmirrored.Length == 0,
            "these wire constants are published in docs/reference/wire.md and named nowhere in "
            + "@shenora/react, so a page must type the raw strings:\n  " + string.Join("\n  ", unmirrored)
            + "\nEither add them to the client, or add the family to HostOnlyFamilies with the reason.");
    }

    /// <summary>
    /// Wire families a PAGE never names, so the client deliberately carries no constant for them.
    /// ⚠ Each entry is a claim that no page-side code needs it — the same claim that was wrong about
    /// the media-conversion events, so justify a new one rather than adding it to make a test pass.
    /// </summary>
    private static readonly HashSet<string> HostOnlyFamilies = new(StringComparer.Ordinal)
    {
        // Sessions are driven from C#: a session is the app's own extra browser, and the documented
        // way to watch one is `bus.Subscribe(...)` host-side (docs/guides/sessions.md). No page sees
        // these — a page cannot reach another session's event bus at all.
        "SessionEvents",
        "InteractiveSessionErrorCodes",
    };

    /// <summary>A repo-relative path, resolved from the test output dir like <see cref="ClientSource"/>.</summary>
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return Path.Combine([dir!, .. parts]);
    }
}
