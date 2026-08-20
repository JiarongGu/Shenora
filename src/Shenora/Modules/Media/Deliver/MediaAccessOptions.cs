using Microsoft.Extensions.Logging;

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
    /// Has a CONVERSION route already been registered on this object? Set by <c>UseMediaConversion</c>, read
    /// by <c>UseComputedRemux</c>, which REPORTS when it finds itself registered second.
    /// </summary>
    /// <remarks>
    /// Middleware run in registration order, so a conversion route registered FIRST answers every request
    /// its own <see cref="Resolve"/> matches — a plannable film then <c>503</c>s through a whole transcode
    /// and the computed route becomes dead code that still passes every test of its own. ⚠ It reports rather
    /// than THROWS: the order bites only when the two routes' predicates OVERLAP, and a predicate is an
    /// opaque function, so nothing here can decide whether a given pair collides.
    /// </remarks>
    internal bool ConversionRegistered { get; set; }
}
