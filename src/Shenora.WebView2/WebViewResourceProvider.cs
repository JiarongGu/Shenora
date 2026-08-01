using System.Collections.Concurrent;
using System.Reflection;

namespace Shenora.WebView2;

/// <summary>
/// Serves the packaged frontend bundle to <see cref="WebViewHost"/>'s virtual host. Implementations
/// must be fast and non-blocking: the virtual-host path serves the MAIN DOCUMENT synchronously on
/// the UI thread (see <see cref="WebViewHost"/> for why), so a stream here should come from memory
/// or an already-warm cache, never a slow device.
/// </summary>
public interface IWebViewResourceProvider
{
    /// <summary>The resource at <paramref name="virtualPath"/> (e.g. <c>index.html</c>,
    /// <c>assets/index-abc123.js</c>), or null when absent.</summary>
    Stream? GetResourceStream(string virtualPath);

    /// <summary>True when <paramref name="virtualPath"/> resolves to a resource.</summary>
    bool Exists(string virtualPath);
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
    /// <c>MyApp.wwwroot.assets.x.js</c> — so the request path and the virtual path are the same
    /// string and no reverse name→path parsing exists (the source app parsed names back to paths,
    /// which mis-mapped any filename containing a dot).
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
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The packaged-frontend resource provider: embedded manifest resources with an in-memory cache,
/// or a plain directory in file mode. Ported from the family's provider with its known gap fixed:
/// the source preloaded EVERY resource with a parallel loop in the constructor, blocking startup —
/// here the cache is lazy (first request pays one manifest read) and <see cref="BeginWarmup"/>
/// optionally fills it in the background, so startup cost is zero either way.
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

        // A provider that can serve NOTHING is reported here but NOT rejected (P5.5 H3). Rejecting in
        // the constructor was the obvious move and is wrong: dev mode navigates to the Vite DevUrl, so
        // the provider is legitimately never consulted, and a fresh clone has an empty wwwroot (the
        // sample's csproj documents exactly that). The loud failure belongs at the point the host
        // COMMITS to serving the bundle — see WebViewHost's startup sanity check, which is what turns a
        // mistyped ResourcePrefix from a black window into an actionable error.
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
                return $"[Shenora.WebView2] Resource provider: SERVES NOTHING — no embedded resources match " +
                       $"'{options.ResourcePrefix}' in '{options.Assembly.GetName().Name}', and no usable " +
                       $"{nameof(EmbeddedResourceProviderOptions.FileFallbackDirectory)} is configured " +
                       $"(PreferFiles={options.PreferFiles}, directory='{options.FileFallbackDirectory ?? "<null>"}'). " +
                       $"Fine if the page loads from a dev URL; otherwise every request will 404. {hint}.";
            });
            return;
        }

        Log(() => IsEmbedded
            ? $"[Shenora.WebView2] Resource provider: EMBEDDED ({_manifest.Count} resources under {options.ResourcePrefix})"
            : $"[Shenora.WebView2] Resource provider: FILE-BASED ({options.FileFallbackDirectory ?? "no directory configured"})");
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="Shenora.Core.AppCallback.Log"/>). This type used
    /// to call <c>_options.Log?.Invoke(…)</c> directly at seven sites; two of them sit inside
    /// <see cref="BeginWarmup"/>'s fire-and-forget <c>Task.Run</c>, where a throwing sink escapes the
    /// very <c>catch</c> it reports from and becomes an unobserved task exception. Laziness also
    /// matters for the constructor's "serves nothing" hint, which enumerates the assembly's manifest
    /// to compose itself.
    /// </summary>
    private void Log(Func<string> message) => Shenora.Core.AppCallback.Log(_options.Log, message);

    /// <summary>True = serving embedded resources; false = serving <see cref="EmbeddedResourceProviderOptions.FileFallbackDirectory"/>.</summary>
    public bool IsEmbedded { get; }

    /// <summary>
    /// False when this provider has NOTHING to serve — no embedded resource matches
    /// <see cref="EmbeddedResourceProviderOptions.ResourcePrefix"/> and no usable
    /// <see cref="EmbeddedResourceProviderOptions.FileFallbackDirectory"/> exists — so every request
    /// would 404. Legitimate when the page loads from a dev URL instead; fatal when the bundle IS the
    /// document, which is why <see cref="WebViewHost"/> checks it at startup rather than the constructor
    /// rejecting it outright (P5.5 H3).
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
                    // GUARDED (Shenora.Core.AppCallback), unlike this type's other Log calls: those sit
                    // under a caller that would observe a throwing sink, whereas this body is a
                    // fire-and-forget Task.Run with nothing above it — an app logger throwing here
                    // escapes the very catch it is reporting from and becomes an unobserved task
                    // exception. An ILogger/Log action IS an app callback (webview2-hosting.md).
                    Log(() => $"[Shenora.WebView2] Warmup failed for {name}: {ex.Message}");
                }
            }
            Log(() => $"[Shenora.WebView2] Resource warmup complete ({_cache.Count} cached)");
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
                Log(() => $"[Shenora.WebView2] Rejected out-of-root resource path: {virtualPath}");
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
                Log(() => $"[Shenora.WebView2] File read failed for {virtualPath}: {ex.Message}");
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
            Log(() => $"[Shenora.WebView2] Resource load failed for {virtualPath}: {ex.Message}");
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
    /// The first two dot-separated segments of a manifest name — enough to recognise a bundle root
    /// (<c>MyApp.wwwroot</c>) in the "did you mean" hint without dumping every file name.
    /// </summary>
    private static string TopTwoSegments(string manifestName)
    {
        var first = manifestName.IndexOf('.');
        if (first < 0) return manifestName;
        var second = manifestName.IndexOf('.', first + 1);
        return second < 0 ? manifestName : manifestName[..second];
    }

    /// <summary>Deterministic virtual-path → manifest-name mapping (slashes become dots).</summary>
    internal string ResourceName(string normalizedVirtualPath) =>
        _options.ResourcePrefix + "." + normalizedVirtualPath.Replace('/', '.');

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Map a normalized virtual path to a file under <paramref name="root"/>, or null when it would
    /// escape. File-mode serving is REACHABLE BY PAGE CONTENT and had no containment at all
    /// (found in the P0–P5 review): the host unescapes the request path before calling us — it must,
    /// so bundle filenames with spaces or CJK characters resolve — so two vectors existed.
    /// (1) <c>%2e%2e%2f…</c> arrives here as <c>../</c> and walked out of the bundle. (2) A ROOTED
    /// path (<c>/C:%2fUsers%2f…</c>) is worse: <see cref="Path.Combine(string,string)"/> DISCARDS the
    /// first argument when the second is rooted, so it returned the caller's absolute path verbatim.
    /// Responses are served with <c>Access-Control-Allow-Origin: *</c>, so any script in the page
    /// could read and exfiltrate what it got back. Embedded mode was safe only incidentally
    /// (<c>../</c> yields a manifest name that doesn't exist).
    /// Both checks matter: rejecting <c>..</c> alone leaves the rooted vector open, and the
    /// full-path prefix assertion alone would still let a rooted path through on some inputs.
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

        // Belt-and-braces: the resolved path must still sit under the root. Compare with the
        // separator appended so "/bundle-evil" can't pass as a child of "/bundle".
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
