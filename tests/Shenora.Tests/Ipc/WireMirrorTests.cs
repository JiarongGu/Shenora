using System.Text.Json;
using System.Text.RegularExpressions;
using Shenora;
using Shenora.Windows;
using Shenora.Modules.FileDialog;
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
    private static string ClientSource(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        var path = Path.Combine(dir!, "src", "Shenora.React", "src", fileName);
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
        // The optional `extends` clause is what the file-dialog options needed (2026-08-05): without it
        // the pattern demanded `{` straight after the name, so `export interface OpenFileOptions extends
        // FileDialogOptions {` matched NOTHING and the test failed claiming the interface did not exist.
        // Fixed in the PARSER, per this file's own rule — the alternative reading ("the mirror is fine,
        // loosen the check") is how a tripwire stops checking anything.
        var block = Regex.Match(source,
            $@"export\s+interface\s+{Regex.Escape(exportName)}(?:\s+extends\s+[^{{]+)?\s*\{{(?<body>.*?)\}}",
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

        var routes = Regex.Matches(source, @"\.send<[^>]*>\('(?<route>[A-Z_]+)'")
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
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
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
}
