namespace Shenora.Modules.Media;

/// <summary>
/// The URL convention <c>MediaPlayer</c> emits and <c>UseMediaPlayer</c>'s route reads back — the string
/// the plan has to survive as.
///
/// <para>
/// 🔴 <b>Why it is a type and not two lambdas.</b> The encoder lives in the player's <c>ResolveUri</c> and
/// the decoder in the route's <c>Resolve</c>/<c>ResolveAction</c>. Written inline they were three anonymous
/// functions that had to agree about a prefix, a separator and an escaping rule — agreement by proximity,
/// which lasts exactly as long as nobody edits one of them. Here they are one thing with a round-trip test.
/// </para>
/// <para>
/// <b>What travels: the SOURCE and the planner's VERDICT.</b> The verdict matters because the plan is
/// computed in the player against the app's <see cref="MediaPlaybackPolicy"/> and the device's
/// <see cref="IMediaCapability"/>, and the route sees only a string — so anything not encoded here is
/// re-derived by a converter that never had either. That is <c>policy → plan → converter</c> closing.
/// </para>
/// <para>
/// ⚠ Internal on purpose. An app wanting its own URL shape sets <c>MediaPlayerOptions.ResolveUri</c> and
/// registers <c>UseMediaConversion</c> itself; this is the kit's DEFAULT convention, not a contract to
/// build against.
/// </para>
/// </summary>
internal static class MediaPlayerRoute
{
    private const string Prefix = "/__shenora/media?src=";
    private const string ActionKey = "&do=";

    /// <summary>The URL for a source the planner says needs work.</summary>
    internal static string Build(string source, MediaPlaybackAction action) =>
        Prefix + Uri.EscapeDataString(source) + ActionKey + action;

    /// <summary>
    /// The source this URL names, or <c>null</c> when it is not one of ours (the pipeline falls through).
    /// <para>
    /// ⚠ Split on the LAST marker, not the first: the action is APPENDED, so a source that itself contains
    /// <c>&amp;do=</c> would otherwise be truncated. Escaping makes that unlikely; splitting from the right
    /// makes it impossible.
    /// </para>
    /// </summary>
    internal static string? SourceOf(string pathAndQuery)
    {
        if (!pathAndQuery.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = pathAndQuery[Prefix.Length..];
        var mark = rest.LastIndexOf(ActionKey, StringComparison.Ordinal);
        return Uri.UnescapeDataString(mark < 0 ? rest : rest[..mark]);
    }

    /// <summary>
    /// The planner's verdict, or <see cref="MediaPlaybackAction.Remux"/> when absent or unreadable — the
    /// cheaper repair, and the right assumption for a request that reached a conversion route at all.
    /// </summary>
    internal static MediaPlaybackAction ActionOf(string pathAndQuery)
    {
        var mark = pathAndQuery.LastIndexOf(ActionKey, StringComparison.Ordinal);
        return mark >= 0
            && Enum.TryParse<MediaPlaybackAction>(pathAndQuery[(mark + ActionKey.Length)..], out var parsed)
            ? parsed
            : MediaPlaybackAction.Remux;
    }
}
