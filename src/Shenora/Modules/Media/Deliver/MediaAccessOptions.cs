using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;

namespace Shenora.Modules.Media;

/// <summary>
/// Where media may be read from, where derived files are kept, and which module the routes speak on —
/// stated ONCE for every delivery path, because three copies of a containment boundary are three chances
/// to get it wrong (D71).
/// </summary>
public sealed class MediaAccessOptions
{
    /// <summary>Map a request URL to a file, or null for "not mine" so the pipeline falls through.</summary>
    public required Func<Uri, string?> Resolve { get; init; }

    /// <summary>
    /// The only directories a resolved path may live under. 🔴 Empty means NOTHING is servable, so a missing
    /// configuration fails closed rather than serving the disk.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>Where derived files are written. ⚠ Not the segment cache root — see D71 on pinning.</summary>
    public required string CacheRoot { get; init; }

    /// <summary>
    /// The module the routes publish on. ⚠ The <c>SHENORA.</c> prefix is RESERVED for the kit (D64).
    /// </summary>
    public string Module { get; init; } = "SHENORA.MEDIA";

    /// <summary>Diagnostics. Guarded at every call site — a throwing sink never escapes.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// How many CONVERSION routes on this object are mounted on each interceptor. Counted by
    /// <c>UseMediaConversion</c> and its disposal; read by <c>UseComputedRemux</c>, which refuses to mount
    /// behind one.
    /// </summary>
    /// <remarks>
    /// Both routes match requests through this object's <see cref="Resolve"/>, and the conversion route
    /// answers every request it matches. Middleware run in registration order, so on the same interceptor a
    /// conversion route mounted first leaves the computed route nothing to serve. ⚠ Keyed per interceptor:
    /// one options object shared by two webviews mounts both routes on each, in order, and is correct.
    /// Separate options objects are not compared — their predicates are opaque and may not overlap.
    /// </remarks>
    internal ConditionalWeakTable<IWebViewInterceptor, StrongBox<int>> ConversionRoutes { get; } = new();
}
