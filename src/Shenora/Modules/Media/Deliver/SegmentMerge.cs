namespace Shenora.Modules.Media;

/// <summary>What became of a request to turn a finished stream into one file.</summary>
public enum SegmentMergeOutcome
{
    /// <summary>The file was written.</summary>
    Written,

    /// <summary>Segments are still missing, so there is nothing to merge yet.</summary>
    Incomplete,

    /// <summary>The source is not one this route serves, or has never been asked for.</summary>
    UnknownSource,

    /// <summary>
    /// The destination was refused. ⚠ The commonest cause is asking for it INSIDE the segment cache —
    /// see <see cref="ISegmentStreamRoute.MergeAsync"/>.
    /// </summary>
    DestinationRefused,

    /// <summary>The write failed. The detail says what, without a path.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="ISegmentStreamRoute.MergeAsync"/>, and why.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A sentence for a log. ⚠ Never a path — this can reach an app's own error surface.</param>
public sealed record SegmentMergeResult(SegmentMergeOutcome Outcome, string Detail)
{
    /// <summary>True only for <see cref="SegmentMergeOutcome.Written"/>.</summary>
    public bool Ok => Outcome is SegmentMergeOutcome.Written;
}

/// <summary>
/// The handle <c>UseSegmentStream</c> returns: dispose it to remove the route, and ask it when a stream has
/// finished producing.
/// <para>
/// 🔴 <b>"We have every segment" and "we have the finished file" are ONE state</b> (D71): the segments
/// already on disk ARE the artifact, in order, so merging is a copy rather than a second production. The
/// APP asks, in .NET, and the page contract does not change.
/// </para>
/// </summary>
public interface ISegmentStreamRoute : IDisposable
{
    /// <summary>
    /// Has every segment the manifest names been produced? Every index on the plan exists and is non-empty,
    /// and so does the initialisation segment. False for a source this route has never opened.
    /// </summary>
    /// <param name="source">The same file path the route's own resolver returns.</param>
    bool IsComplete(string source);

    /// <summary>
    /// Concatenate a finished stream into ONE fragmented MP4 at <paramref name="destination"/>. The
    /// initialisation segment followed by every fragment in plan order IS a valid fMP4, so this is a byte
    /// copy; written to a temporary path and moved into place.
    /// <para>
    /// 🔴 <b>The destination may NOT be inside the segment cache, and this is enforced.</b> The two have
    /// OPPOSITE policies — the cache is evicted oldest-used first under a byte cap, a persisted artifact
    /// never — so writing one into the other means ordinary playback silently deletes a file someone waited
    /// for, surfacing much later as a download that used to work.
    /// </para>
    /// </summary>
    /// <param name="source">The source whose stream to merge.</param>
    /// <param name="destination">Where to write it. Must be outside the cache root; parents are created.</param>
    /// <param name="cancellationToken">Abandons the copy and leaves the temporary file removed.</param>
    Task<SegmentMergeResult> MergeAsync(string source, string destination,
                                                 CancellationToken cancellationToken = default);
}

/// <summary>The pure half of piece 5: what "complete" means, and how the pieces become one file.</summary>
internal static class SegmentMerge
{
    /// <summary>Copied in this size — big enough that a two-hour film is not a million calls.</summary>
    private const int CopyBuffer = 128 * 1024;

    /// <summary>Every file the artifact is made of, in the order it must be written.</summary>
    internal static IReadOnlyList<string> Parts(string directory, SegmentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>(plan.Count + 1)
        {
            Path.Combine(directory, SegmentRunRequest.InitSegmentName),
        };

        for (var i = 0; i < plan.Count; i++)
        {
            parts.Add(Path.Combine(directory, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"seg{i}{SegmentRunRequest.SegmentExtension}")));
        }

        return parts;
    }

    /// <summary>
    /// Is every part present and non-empty? ⚠ Non-empty, not merely present: a run killed mid-write leaves
    /// a file that exists and holds nothing, and merging that gives a film that plays for two seconds.
    /// </summary>
    internal static bool IsComplete(string directory, SegmentPlan plan)
    {
        foreach (var part in Parts(directory, plan))
        {
            try
            {
                var info = new FileInfo(part);
                if (!info.Exists || info.Length == 0) return false;
            }
            catch (Exception)
            {
                // A part that cannot be stat'ed is a part we cannot claim to have.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Would writing to <paramref name="destination"/> put the artifact inside the evictable cache? Compared
    /// on FULL PATHS with a trailing separator, so <c>…/cache-of-mine</c> is not inside <c>…/cache</c>.
    /// </summary>
    internal static bool IsInside(string destination, string cacheRoot)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(destination);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unusable path is refused by the caller anyway; saying "inside" here would be a guess.
            return false;
        }
    }

    /// <summary>Concatenate the parts into <paramref name="destination"/> through a temporary file.</summary>
    internal static async Task WriteAsync(IReadOnlyList<string> parts, string destination,
                                          CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // ⚠ The temporary sits BESIDE the destination, not in the system temp: a move across volumes is a
        // copy, which reintroduces the torn file this exists to prevent.
        var temporary = destination + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                                                     CopyBuffer, useAsync: true))
            {
                foreach (var part in parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                           CopyBuffer, useAsync: true);
                    await input.CopyToAsync(output, CopyBuffer, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            // Leave nothing a later run could mistake for a finished artifact.
            try { File.Delete(temporary); } catch (Exception) { /* it was never usable */ }
            throw;
        }
    }
}
