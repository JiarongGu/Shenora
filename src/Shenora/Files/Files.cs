using Shenora.Missions;

using System.Text;

namespace Shenora;

/// <summary>How a write reaches the target file. Names the PROCESS, not a kind of file.</summary>
public enum FileWriteMode
{
    /// <summary>
    /// Produce the contents beside the target, flush them to disk, then rename over it — the DEFAULT,
    /// because it is the only way a reader can never observe a half-written file and an interruption
    /// can never destroy the previous one. Costs one extra copy on disk until the rename.
    /// </summary>
    Atomic = 0,

    /// <summary>
    /// Truncate the target and write into it — what <see cref="File.WriteAllText(string,string)"/>
    /// does, still flushed to disk. **An interruption leaves the target torn**, so choose it
    /// deliberately, for the two cases where <see cref="Atomic"/> genuinely cannot pay:
    /// a very large file, where the temp doubles peak disk use; and a filesystem that will not honour
    /// the rename (some network shares and FUSE mounts). Anywhere else this is the old bug with a
    /// nicer spelling.
    /// </summary>
    Direct = 1,
}

/// <summary>
/// Write a file. The kit's counterpart to <see cref="File"/>, one file at a time, synchronously.
///
/// <para><b>Every write here is atomic, and that is not a feature — it is the only correct way to
/// replace a file's contents.</b> An earlier draft called this <c>AtomicFile</c>, which framed
/// atomicity as a mode you opt into; the trouble with an opt-in is that the call sites which forget
/// are exactly the ones that break, and "atomic" describes the OPERATION anyway, never the file.
/// So there is no non-atomic option here. If you want a torn write you already have
/// <see cref="File"/>.
/// </para>
///
/// <para><b>Named <c>Files</c>, one letter from <see cref="File"/>, deliberately.</b> The mental
/// substitution is meant to be that cheap. It cannot be called <c>File</c>: a consumer with both
/// <c>using System.IO;</c> and <c>using Shenora;</c> would get an ambiguity error on every
/// existing <c>File.</c> call in the file, which is a hostile thing to do to an adopter.
/// </para>
///
/// <para><b>Why this exists separately from the update queue</b> (<c>IFileUpdateQueue</c>, in the
/// optional <c>Shenora.IO</c> package — named as text rather than a cref because Core cannot see it,
/// which is the point: this primitive is available to every consumer, the queue is not). The queue is for
/// MULTI-change, cross-process, rollback-able work: N files that must land together, partitioned so two
/// processes cannot interleave. Most file writing is not that. A config store is one file, synchronous
/// and best-effort, and at least one of them saves from a window-closing path where awaiting a queue is
/// actively worse. The queue already owned the concept — <c>FileChange.Replace</c> — but only through
/// <c>ApplyAsync</c>, so every app rewrote this by hand.
/// </para>
///
/// <para><b>The failure being prevented is silent.</b> <see cref="File.WriteAllText(string,string)"/>
/// TRUNCATES the target and then writes into it. Config stores typically load best-effort (corrupt ⇒
/// fall back to defaults), so an interrupted write does not fail loudly — it silently resets the user's
/// settings, and nobody notices until they wonder why their preferences reverted.
/// </para>
///
/// <para><b>Ported from the first adopter</b>, which wrote it as an explicit stopgap and filed it
/// upstream the same day. Its four hard-won details are kept and commented below; the tests came with
/// it. Extraction-first (D8), not a redesign.
/// </para>
/// </summary>
public static class Files
{
    /// <summary>
    /// The suffix appended to the target path to form the temp file.
    /// <para>
    /// FIXED, not random, and deliberately so: a crash before the rename leaves ONE predictable leftover
    /// that the next successful write overwrites, instead of accumulating debris nobody sweeps. The
    /// trade is that two concurrent writers of the same path share it — fine for a config store, where
    /// last-writer-wins is the intended semantics, and NOT fine for a long
    /// <see cref="BeginReplace(string,string)"/>, which is why that overload lets you pass your own.
    /// </para>
    /// </summary>
    public const string DefaultTempSuffix = ".tmp";

    /// <summary>
    /// The default text encoding: UTF-8 with NO byte-order mark.
    /// <para>
    /// A default, not a rule — pass your own to <see cref="WriteAllText"/> if you need otherwise. It is
    /// the right default because a BOM is a silent format change for a file other tools already parse
    /// (a shell script, a launcher doing a substring read, anything expecting plain JSON), and it is
    /// PARAMETERISED because that is a consumer's decision: an app talking to a legacy Windows tool may
    /// genuinely need the BOM, and hard-coding its absence would have made this unusable for them.
    /// </para>
    /// </summary>
    public static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write text, replacing the file atomically. Throws on failure, leaving the previous file intact.
    /// </summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="contents">The new contents.</param>
    /// <param name="encoding">Defaults to <see cref="DefaultEncoding"/> (UTF-8, no BOM).</param>
    /// <param name="mode">Defaults to <see cref="FileWriteMode.Atomic"/>.</param>
    public static void WriteAllText(string path, string contents, Encoding? encoding = null,
                                    FileWriteMode mode = FileWriteMode.Atomic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding ?? DefaultEncoding, leaveOpen: true);
            writer.Write(contents);
        }, mode);
    }

    /// <summary>Write raw bytes. The binary twin of <see cref="WriteAllText"/>.</summary>
    public static void WriteAllBytes(string path, byte[] contents,
                                     FileWriteMode mode = FileWriteMode.Atomic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        Write(path, stream => stream.Write(contents, 0, contents.Length), mode);
    }

    /// <summary>
    /// Produce the new contents into a stream, then swap them in — serialize straight into it rather
    /// than buffering a whole file to hand to <see cref="WriteAllBytes"/>.
    ///
    /// <para><b>Throws on failure, like <see cref="File.WriteAllText(string,string)"/> does, and the
    /// previous file is left intact.</b> An earlier draft returned <c>bool</c> and never threw, which
    /// was the first adopter's config-store POLICY rather than a mechanism: a caller that ignores the
    /// result then carries on with a stale file — the same silent failure this type exists to prevent,
    /// one level up. A best-effort caller writes the policy it wants:
    /// </para>
    /// <code>
    /// try { Files.WriteAllText(path, json); }
    /// catch (Exception ex) { _log.Warn(ex, "settings save failed; previous file kept"); }
    /// </code>
    /// </summary>
    public static void Write(string path, Action<Stream> write, FileWriteMode mode = FileWriteMode.Atomic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        if (mode == FileWriteMode.Direct)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            write(target);
            // Still flushed to disk: Direct gives up ATOMICITY, not durability. The only difference
            // from Atomic is that an interruption can leave this target torn — which is the trade the
            // caller opted into, not an excuse to also lose the write that did finish.
            target.Flush(flushToDisk: true);
            return;
        }

        using var replacement = BeginReplace(path);
        using (var stream = new FileStream(replacement.TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            write(stream);
        }
        replacement.Commit();
        // Anything thrown above escapes deliberately, and Dispose still discards the temp on the way
        // out — so the guarantee holds whether the caller catches or not: the PREVIOUS file survives.
    }

    /// <summary>
    /// Begin a replacement: get a temp path beside <paramref name="targetPath"/>, produce whatever you
    /// like into it — an encode, a compile, an extraction, a render — and
    /// <see cref="FileReplacement.Commit"/> when it is good. Disposing without committing discards it.
    ///
    /// <para><b>The input is never touched.</b> That is the whole point, and it is what makes this
    /// different from writing over the target as you go: an interruption costs the WORK, never the
    /// original. The longer the operation, the wider the window a naive in-place write leaves open,
    /// which is why "just write it and hope" fails in production and not in testing.
    /// </para>
    ///
    /// <para><b>Verify before you commit.</b> "Finished writing" is not "valid" — a truncated encode is
    /// fully written and worthless, and swapping it in destroys the original just as surely as writing
    /// over it would have. Only the caller knows what valid means for its format, so the kit does not
    /// guess: check the temp, then commit or let it go.
    /// </para>
    ///
    /// <para><b>Concurrency is the caller's.</b> Two transforms of one target sharing a temp suffix
    /// will collide. Either pass distinct suffixes or hold a <see cref="MissionClaim"/> on the path —
    /// which is what <see cref="IMissionScheduler"/> is for, and a long transform belongs there anyway.
    /// </para>
    /// </summary>
    /// <param name="targetPath">The file to replace when the replacement commits.</param>
    /// <param name="tempSuffix">Appended to the target path; see <see cref="DefaultTempSuffix"/>.</param>
    public static FileReplacement BeginReplace(string targetPath, string tempSuffix = DefaultTempSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempSuffix);
        return new FileReplacement(targetPath, tempSuffix);
    }
}

/// <summary>
/// One in-flight atomic replacement — see <see cref="Files.BeginReplace(string,string)"/>.
/// Produce into <see cref="TempPath"/>, then <see cref="Commit"/>. Disposing without committing
/// discards the temp, so a <c>using</c> that throws cleans up on its way out.
/// </summary>
public sealed class FileReplacement : IDisposable
{
    private bool _committed;
    private bool _disposed;

    internal FileReplacement(string targetPath, string tempSuffix)
    {
        TargetPath = targetPath;
        TempPath = targetPath + tempSuffix;

        // The temp is a SIBLING of the target, never in the system temp folder: a rename is only atomic
        // WITHIN a volume, and across volumes it silently degrades to copy-then-delete — losing exactly
        // the property this type exists for. The caller cannot get that wrong because it never chooses
        // the path.
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>The file that will be replaced on <see cref="Commit"/>.</summary>
    public string TargetPath { get; }

    /// <summary>Produce the new contents here. A sibling of <see cref="TargetPath"/>, on the same volume.</summary>
    public string TempPath { get; }

    /// <summary>
    /// Flush the temp to disk and rename it over the target. Throws on failure, leaving the previous
    /// file intact; calling it again after it succeeded is a no-op.
    /// </summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) return;

        FlushToDisk(TempPath);

        // File.Move over the target, NOT File.Replace. Move needs no backup path and does not care
        // whether the target already exists, so one call covers the first write and every later one.
        // (The queue uses File.Replace because it needs the displaced original to roll back — a
        // different job. Here there is nothing to roll back to: the previous file is either replaced
        // or untouched.)
        File.Move(TempPath, TargetPath, overwrite: true);
        _committed = true;
    }

    /// <summary>Discards the temp file unless <see cref="Commit"/> succeeded.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_committed) return;
        try { if (File.Exists(TempPath)) File.Delete(TempPath); }
        catch (Exception) { /* scratch file; nothing useful to do and nobody to tell */ }
    }

    /// <summary>
    /// Force the file's contents out of the OS write cache before the rename.
    /// <para>
    /// WITHOUT THIS THE WHOLE EXERCISE CAN STILL FAIL. The rename is a metadata operation and completes
    /// long before the data does, so a power loss can leave an intact rename pointing at an EMPTY file —
    /// precisely the outcome the rename was supposed to prevent. Opening the finished file and flushing
    /// its handle covers the case where something ELSE wrote it (an encoder, a compiler), which a flush
    /// on our own writer cannot.
    /// </para>
    /// <para>
    /// ⚠ <b>NOT COVERED BY A TEST, and measured rather than assumed:</b> deleting this call leaves all
    /// of <c>FilesTests</c> green. Durability against power loss cannot be asserted from a process
    /// that is still running — you would need to actually cut power between the write and the rename.
    /// Every other guarantee here is sabotage-verified; this one rests on the reasoning above, so treat
    /// it as load-bearing and do not "simplify" it away because nothing went red.
    /// </para>
    /// </summary>
    private static void FlushToDisk(string path)
    {
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Flush(flushToDisk: true);
    }
}
