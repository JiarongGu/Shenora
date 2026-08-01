using System.Text.Json;
using System.Text.RegularExpressions;
using Shenora.Ipc;
using Shenora.WebView2;

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
        return File.ReadAllText(path);
    }

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
        var block = Regex.Match(source,
            $@"export\s+interface\s+{Regex.Escape(exportName)}\s*\{{(?<body>.*?)\}}",
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
            "Either add them to Shenora.Ipc.IpcErrorCodes or list them in ClientOnlyIpcErrorCodes.");
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
    /// <see cref="OperationStatus"/> (design §4.6/§9.1) crosses the wire as its camelCase name for
    /// free — <see cref="IpcJson"/> installs a camelCase <c>JsonStringEnumConverter</c> — so the
    /// client's <c>OperationStatuses</c> const object must name exactly the same set of strings.
    /// A status added on one side and not the other must fail THIS test by name, not pass a green
    /// suite that never compared the two sets (the same disease <c>SCOPE_REQUIRED</c> had).
    /// </summary>
    [Fact]
    public void Every_operation_status_exists_on_both_sides()
    {
        var host = Enum.GetNames<OperationStatus>()
            .Select(n => JsonNamingPolicy.CamelCase.ConvertName(n))
            .ToHashSet(StringComparer.Ordinal);
        var client = ParseConstObject(ClientSource("operations.ts"), "OperationStatuses").Values.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(host);      // parser self-check: a regex that matched nothing must not pass
        Assert.NotEmpty(client);
        Assert.Equal(host, client);
    }

    /// <summary>
    /// ALSO IN THIS BATCH (whole-branch review): the client hardcodes <c>'OPERATION_UPDATED'</c>,
    /// <c>'LIST'</c>, <c>'CANCEL'</c>, <c>'CLEAR_FINISHED'</c>, <c>'RESUME'</c> and the
    /// <c>'OPERATIONS'</c> module name with nothing comparing them against
    /// <see cref="OperationEvents"/>/<see cref="OperationsFacade"/>/
    /// <see cref="OperationRegistryOptions.ModuleName"/> — a host rename left the suite green and the
    /// client deaf, the exact disease <see cref="Every_host_error_code_exists_on_the_client_and_vice_versa"/>
    /// already exists to catch for error codes.
    /// </summary>
    [Fact]
    public void Operation_event_names_match_the_host()
    {
        var client = ParseConstObject(ClientSource("operations.ts"), "OperationEventTypes");

        Assert.NotEmpty(client);   // parser self-check: a regex that matched nothing must not pass
        Assert.Equal(OperationEvents.Updated, client["Updated"]);
        Assert.Equal(OperationEvents.ResumeRequested, client["ResumeRequested"]);
        // Generic-library audit finding 3: WAIT_REQUESTED (renamed from PAUSE_REQUESTED) is new — pin
        // it the same way, or a host rename leaves the client deaf to it exactly like the others this
        // test already guards.
        Assert.Equal(OperationEvents.WaitRequested, client["WaitRequested"]);
    }

    [Fact]
    public void Operation_route_names_match_the_hosts_facade()
    {
        var client = ParseConstObject(ClientSource("operations.ts"), "OperationRoutes");

        Assert.NotEmpty(client);   // parser self-check
        Assert.Equal(OperationsFacade.ListType, client["List"]);
        Assert.Equal(OperationsFacade.CancelType, client["Cancel"]);
        Assert.Equal(OperationsFacade.ClearFinishedType, client["ClearFinished"]);
        Assert.Equal(OperationsFacade.ResumeType, client["Resume"]);
        // RESUME/DISMISS are the human's decisions (§5A.3 amendment); WAIT (generic-library audit
        // finding 3, renamed from PAUSE) is the client ASKING the host to wait — see OperationsFacade's
        // own class doc.
        Assert.Equal(OperationsFacade.DismissType, client["Dismiss"]);
        Assert.Equal(OperationsFacade.WaitType, client["Wait"]);
    }

    [Fact]
    public void The_default_operations_module_name_matches_the_host()
    {
        var source = ClientSource("operations.ts");

        Assert.Equal(new OperationRegistryOptions().ModuleName, ParseExportedString(source, "OperationModuleName"));
    }

    /// <summary>
    /// <see cref="OperationProgress"/> replaced a bare 0–100 <c>int?</c> (generic-library audit, before
    /// publish) — a NEW wire shape both sides name, so it needs its own tripwire. This compares the
    /// SET of field names (camelCased) rather than trusting the two sides to stay in step by
    /// inspection — the exact disease this whole file exists to catch for everything else on this wire.
    /// </summary>
    [Fact]
    public void OperationProgress_fields_match_the_host()
    {
        AssertMirroredFields(typeof(OperationProgress), "OperationProgress");
    }

    /// <summary>
    /// <b><see cref="OperationInfo"/> is the biggest shape on this wire and had NO mirror at all until
    /// the 0.2.0 design pass</b> — it is both the entire <c>OPERATION_UPDATED</c> payload and the
    /// element type of the <c>LIST</c> response, so a field present on one side and not the other is a
    /// silent hole in every operation-driven UI.
    /// <para>
    /// It was missed because of a plausible-sounding claim, which is why this comment records it:
    /// <see cref="OperationProgress_fields_match_the_host"/>'s own doc used to assert that
    /// "<c>OperationInfo</c>'s other fields are pinned by <c>[JsonPropertyName]</c> + the API baseline
    /// on the host side". Both halves are true and together they still prove nothing about the MIRROR —
    /// they pin the HOST's names against the HOST's own baseline, and no test compared them to the TS
    /// interface. The smaller, newer type got a tripwire; the one that actually carries the payload did
    /// not. (Found when a whole-codebase review removed <c>ResumePayload</c> from both sides by hand
    /// and nothing verified that both hands had moved.)
    /// </para>
    /// </summary>
    [Fact]
    public void OperationInfo_fields_match_the_host()
    {
        AssertMirroredFields(typeof(OperationInfo), "OperationInfo");
    }

    /// <summary>
    /// The shared mirror check: a host record's property names, camelCased the way
    /// <see cref="IpcJson"/> serializes them, must equal the TS interface's field names exactly.
    /// One helper so a third wire shape cannot be added with a subtly weaker check.
    /// </summary>
    private static void AssertMirroredFields(Type hostType, string clientInterface)
    {
        var host = hostType.GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);
        var client = ParseInterfaceFieldNames(ClientSource("operations.ts"), clientInterface);

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
