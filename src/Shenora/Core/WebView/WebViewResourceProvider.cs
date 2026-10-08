using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shenora.Core.WebView;

/// <summary>
/// Serves the packaged frontend bundle to a shell, at its app origin. 🔴 Implementations must be fast and
/// non-blocking: the WebView2 shell serves the MAIN DOCUMENT from it synchronously on its UI thread, so a
/// stream here comes from memory or an already-warm cache, never a slow device.
/// </summary>
public interface IWebViewResourceProvider
{
    /// <summary>The resource at <paramref name="virtualPath"/> (e.g. <c>index.html</c>,
    /// <c>assets/index-abc123.js</c>), or null when absent.</summary>
    Stream? GetResourceStream(string virtualPath);

    /// <summary>True when <paramref name="virtualPath"/> resolves to a resource.</summary>
    bool Exists(string virtualPath);

    /// <summary>
    /// Optional: start filling any cache in the BACKGROUND so the first navigation does not pay for it.
    /// Called once at startup. Fire-and-forget and idempotent; a provider with nothing to warm does
    /// nothing.
    /// </summary>
    void BeginWarmup() { }
}

/// <summary>Inputs for <see cref="EmbeddedResourceProvider"/>.</summary>
public sealed class EmbeddedResourceProviderOptions
{
    /// <summary>The assembly the frontend bundle is embedded in.</summary>
    public required Assembly Assembly { get; init; }

    /// <summary>
    /// Manifest-name prefix of the bundle root, INCLUDING the folder segment (e.g.
    /// <c>MyApp.wwwroot</c> for <c>&lt;EmbeddedResource Include="wwwroot\**"/&gt;</c> in project
    /// <c>MyApp</c>). Virtual paths are relative to it: <c>assets/x.js</c> ⇒
    /// <c>MyApp.wwwroot.assets.x.js</c>.
    /// </summary>
    public required string ResourcePrefix { get; init; }

    /// <summary>
    /// On-disk bundle root (e.g. <c>&lt;root&gt;/wwwroot</c>) served when
    /// <see cref="PreferFiles"/> is set and the directory exists — the unpackaged/dev fallback.
    /// </summary>
    public string? FileFallbackDirectory { get; init; }

    /// <summary>
    /// Prefer <see cref="FileFallbackDirectory"/> over embedded resources when it exists
    /// (typically wired to the app's development flag). Default: embedded wins.
    /// </summary>
    public bool PreferFiles { get; init; }

    /// <summary>Diagnostics sink. Null = silent.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// The packaged-frontend resource provider: embedded manifest resources with a lazy in-memory cache,
/// or a plain directory in file mode. <see cref="BeginWarmup"/> optionally fills the cache in the
/// background, so startup pays nothing either way.
/// </summary>
public sealed class EmbeddedResourceProvider : IWebViewResourceProvider
{
    private readonly EmbeddedResourceProviderOptions _options;
    // Actual manifest names keyed case-insensitively by themselves (GetManifestResourceStream is
    // case-sensitive; requests may not be).
    private readonly Dictionary<string, string> _manifest;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _warmupStarted;

    /// <summary>Serves the bundle embedded in <see cref="EmbeddedResourceProviderOptions.Assembly"/>.</summary>
    public EmbeddedResourceProvider(EmbeddedResourceProviderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var prefix = options.ResourcePrefix + ".";
        _manifest = options.Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

        var fileMode = options.PreferFiles
                       && options.FileFallbackDirectory is { Length: > 0 } dir
                       && Directory.Exists(dir);
        IsEmbedded = !fileMode && _manifest.Count > 0;

        // A provider that can serve NOTHING is reported here but NOT rejected: dev mode navigates to
        // the Vite DevUrl, so the provider is legitimately never consulted and a fresh clone has an
        // empty wwwroot. The loud failure belongs where the host COMMITS to serving the bundle —
        // the WebView2 shell's start-up check of its start document.
        CanServe = IsEmbedded || fileMode;
        if (!CanServe)
        {
            Log(() =>
            {
                var available = options.Assembly.GetManifestResourceNames();
                var hint = available.Length == 0
                    ? "that assembly embeds NO resources at all — check the <EmbeddedResource> item group"
                    : "available manifest prefixes: " + string.Join(", ",
                        available.Select(TopTwoSegments).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(10));
                return $"[Shenora.Core.WebView] Resource provider: SERVES NOTHING — no embedded resources match " +
                       $"'{options.ResourcePrefix}' in '{options.Assembly.GetName().Name}', and no usable " +
                       $"{nameof(EmbeddedResourceProviderOptions.FileFallbackDirectory)} is configured " +
                       $"(PreferFiles={options.PreferFiles}, directory='{options.FileFallbackDirectory ?? "<null>"}'). " +
                       $"Fine if the page loads from a dev URL; otherwise every request will 404. {hint}.";
            });
            return;
        }

        Log(() => IsEmbedded
            ? $"[Shenora.Core.WebView] Resource provider: EMBEDDED ({_manifest.Count} resources under {options.ResourcePrefix})"
            : $"[Shenora.Core.WebView] Resource provider: FILE-BASED ({options.FileFallbackDirectory ?? "no directory configured"})");
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="Shenora.AppCallback.Log"/>): two call sites sit
    /// inside <see cref="BeginWarmup"/>'s fire-and-forget <c>Task.Run</c>, where a throwing sink
    /// escapes the very <c>catch</c> it reports from, and the constructor's "serves nothing" hint
    /// enumerates the whole manifest to compose itself.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => Shenora.AppCallback.Log(_options.Log, message, exception: failure);

    /// <summary>True = serving embedded resources; false = serving <see cref="EmbeddedResourceProviderOptions.FileFallbackDirectory"/>.</summary>
    public bool IsEmbedded { get; }

    /// <summary>
    /// False when this provider has NOTHING to serve — no embedded resource matches
    /// <see cref="EmbeddedResourceProviderOptions.ResourcePrefix"/> and no usable
    /// <see cref="EmbeddedResourceProviderOptions.FileFallbackDirectory"/> exists — so every request
    /// would 404. Legitimate when the page loads from a dev URL, fatal when the bundle IS the document
    /// (the WebView2 shell refuses to start over it).
    /// </summary>
    public bool CanServe { get; }

    /// <summary>
    /// Start caching every embedded resource in the background (fire-and-forget, idempotent).
    /// Call once at startup so the bundle is memory-resident by the time the first navigation
    /// asks for it; anything requested earlier just loads lazily.
    /// </summary>
    public void BeginWarmup()
    {
        if (!IsEmbedded || Interlocked.Exchange(ref _warmupStarted, 1) != 0) return;
        _ = Task.Run(() =>
        {
            foreach (var name in _manifest.Keys)
            {
                try
                {
                    _ = LoadEmbedded(name);
                }
                catch (Exception ex)
                {
                    Log(() => $"[Shenora.Core.WebView] Warmup failed for {name}", ex);
                }
            }
            Log(() => $"[Shenora.Core.WebView] Resource warmup complete ({_cache.Count} cached)");
        });
    }

    /// <inheritdoc />
    public Stream? GetResourceStream(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return null;
        virtualPath = Normalize(virtualPath);

        if (!IsEmbedded)
        {
            if (_options.FileFallbackDirectory is not { Length: > 0 } root) return null;
            if (ResolveContained(root, virtualPath) is not { } filePath)
            {
                Log(() => $"[Shenora.Core.WebView] Rejected out-of-root resource path: {virtualPath}");
                return null;
            }
            if (!File.Exists(filePath)) return null;
            try
            {
                // Read fully into memory so the file is never held open/locked while WebView2
                // streams it (dev rebuilds overwrite the bundle mid-session).
                return new MemoryStream(File.ReadAllBytes(filePath), writable: false);
            }
            catch (Exception ex)
            {
                Log(() => $"[Shenora.Core.WebView] File read failed for {virtualPath}", ex);
                return null;
            }
        }

        var name = ResourceName(virtualPath);
        if (!_manifest.TryGetValue(name, out var actualName)) return null;
        try
        {
            var bytes = LoadEmbedded(actualName);
            return bytes is null ? null : new MemoryStream(bytes, writable: false);
        }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Core.WebView] Resource load failed for {virtualPath}", ex);
            return null;
        }
    }

    /// <inheritdoc />
    public bool Exists(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return false;
        virtualPath = Normalize(virtualPath);
        if (!IsEmbedded)
        {
            return _options.FileFallbackDirectory is { Length: > 0 } root
                   && ResolveContained(root, virtualPath) is { } filePath
                   && File.Exists(filePath);
        }
        return _manifest.ContainsKey(ResourceName(virtualPath));
    }

    private byte[]? LoadEmbedded(string actualName)
    {
        if (_cache.TryGetValue(actualName, out var cached)) return cached;
        using var stream = _options.Assembly.GetManifestResourceStream(actualName);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return _cache.GetOrAdd(actualName, memory.ToArray());
    }

    /// <summary>
    /// The first two dot-separated segments of a manifest name — enough for the "did you mean" hint to
    /// name a bundle root (<c>MyApp.wwwroot</c>) without dumping every file name.
    /// </summary>
    private static string TopTwoSegments(string manifestName)
    {
        var first = manifestName.IndexOf('.');
        if (first < 0) return manifestName;
        var second = manifestName.IndexOf('.', first + 1);
        return second < 0 ? manifestName : manifestName[..second];
    }

    /// <summary>The manifest name for a path: <c>/</c> between folders becomes <c>.</c>, and each folder is named as
    /// MSBuild names it (<see cref="ManifestFolder"/>).</summary>
    internal string ResourceName(string normalizedVirtualPath)
    {
        var slash = normalizedVirtualPath.LastIndexOf('/');
        if (slash < 0) return _options.ResourcePrefix + "." + normalizedVirtualPath;
        var folders = normalizedVirtualPath[..slash].Split('/').Select(ManifestFolder);
        return _options.ResourcePrefix + "." + string.Join('.', folders) + "." + normalizedVirtualPath[(slash + 1)..];
    }

    /// <summary>
    /// A folder as MSBuild writes it into a manifest name: each dot-separated part made an identifier — a
    /// character no identifier holds becomes <c>_</c>, and a part that cannot start one gains a leading
    /// <c>_</c> (<c>my-lib</c> → <c>my_lib</c>, <c>1.0</c> → <c>_1._0</c>, <c>-lead</c> → <c>_lead</c>).
    /// File names are kept as they are.
    /// </summary>
    internal static string ManifestFolder(string folder) =>
        string.Join('.', folder.Split('.').Select(part =>
        {
            if (part.Length == 0) return part;
            var name = new StringBuilder(part.Length + 1);
            if (IsIdentifierStart(part[0])) name.Append(part[0]);
            else
            {
                name.Append('_');
                if (IsIdentifierPart(part[0])) name.Append(part[0]);
            }
            foreach (var c in part.AsSpan(1)) name.Append(IsIdentifierPart(c) ? c : '_');
            return name.ToString();
        }));

    private static bool IsIdentifierStart(char c) =>
        char.IsLetter(c) || char.GetUnicodeCategory(c) == UnicodeCategory.ConnectorPunctuation;

    private static bool IsIdentifierPart(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.Format;

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Map a normalized virtual path to a file under <paramref name="root"/>, or null when it would
    /// escape. 🔴 File-mode serving is REACHABLE BY PAGE CONTENT, and responses carry
    /// <c>Access-Control-Allow-Origin: *</c>, so any script can read what comes back. The host must
    /// unescape the request path (bundle filenames carry spaces and CJK characters), so two vectors
    /// arrive here: <c>%2e%2e%2f…</c> as <c>../</c>, and a ROOTED path, where
    /// <see cref="Path.Combine(string,string)"/> DISCARDS its first argument and returns the caller's
    /// absolute path verbatim. ⚠ Both checks matter — rejecting <c>..</c> alone leaves the rooted
    /// vector open, and the full-path prefix assertion alone still lets some rooted paths through.
    /// </summary>
    internal static string? ResolveContained(string root, string normalizedVirtualPath)
    {
        var relative = normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar);

        // Reject anything rooted (drive-qualified, UNC, or leading separator) outright — a rooted
        // path is never a legitimate bundle-relative resource request.
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;

        // Reject traversal segments before touching the filesystem.
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..") return null;
        }

        string fullRoot, combined;
        try
        {
            fullRoot = Path.GetFullPath(root);
            combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        }
        catch (Exception)
        {
            return null; // malformed path (invalid characters, too long, …) — never serve it
        }

        // The resolved path must still sit under the root. ⚠ Compare with the separator appended, or
        // "/bundle-evil" passes as a child of "/bundle".
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
