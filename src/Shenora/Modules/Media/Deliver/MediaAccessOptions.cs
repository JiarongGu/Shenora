using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>
/// Where media may be read from, where derived files are kept, and which module the routes speak on —
/// stated ONCE for every delivery path.
///
/// <para>
/// 🔴 <b><see cref="AllowedRoots"/> is a containment boundary, not a convenience</b>, which is why this type
/// exists rather than each options type carrying its own copy. It stops a page-supplied path escaping into
/// the rest of the disk, and the kit deliberately refuses to default it (D61's test: does this decide
/// something the app cares about?). Three copies of a security decision is three chances to get it wrong,
/// and D71 adds a fourth delivery path.
/// </para>
/// </summary>
public sealed class MediaAccessOptions
{
    /// <summary>Map a request URL to a file, or null for "not mine" so the pipeline falls through.</summary>
    public required Func<Uri, string?> Resolve { get; init; }

    /// <summary>
    /// The only directories a resolved path may live under. Empty means NOTHING is servable — deliberately,
    /// so a missing configuration fails closed rather than serving the disk.
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
    /// Has a CONVERSION route already been registered on this object? Set by
    /// <c>UseMediaConversion</c>, read by <c>UseComputedRemux</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This exists so a load-bearing registration ORDER stops being a comment.</b> Middleware run in
    /// registration order, so a conversion route registered FIRST answers every request its own
    /// <see cref="Resolve"/> matches — a plannable film then <c>503</c>s through a whole transcode and the
    /// computed route becomes dead code that still passes every test of its own. Until now the only warning
    /// was prose, in three places, none of which an app's compiler or runtime reads.
    /// </para>
    /// <para>
    /// ⚠ <b>It reports rather than THROWS, and that is not timidity.</b> The order only bites when the two
    /// routes' <see cref="Resolve"/> predicates OVERLAP — the sample's claim different path prefixes, where
    /// either order is correct — and a predicate is an opaque function, so nothing here can decide whether
    /// a given pair collides. Throwing would break a legitimate composition to protect against a possible
    /// one; a line that names the consequence lets the app tell the difference the kit cannot.
    /// </para>
    /// <para>
    /// ⚠ It lives HERE because this is the object both routes already share (D71/D73). A static would be
    /// process-wide and wrong the moment an app hosts two webviews; a new parameter would be a knob nobody
    /// sets.
    /// </para>
    /// </remarks>
    internal bool ConversionRegistered { get; set; }
}
