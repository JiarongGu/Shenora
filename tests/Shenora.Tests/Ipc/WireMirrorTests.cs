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
}
