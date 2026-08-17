using Shenora.Engine.Missions;

using System.Text;

namespace Shenora.Engine.Files;

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
    /// does, still flushed to disk. **An interruption leaves the target torn**, so choose it only for
    /// the two cases where <see cref="Atomic"/> cannot pay: a very large file, where the temp doubles
    /// peak disk use, and a filesystem that will not honour the rename (some network shares, FUSE).
    /// </summary>
    Direct = 1,
}

/// <summary>
/// Write a file. The kit's counterpart to <see cref="File"/>, one file at a time, synchronously —
/// every write atomic unless you ask for <see cref="FileWriteMode.Direct"/>. For MULTI-change,
/// cross-process, rollback-able work (N files that must land together) use
/// <see cref="IFileUpdateQueue"/> instead.
///
/// <para><b>The failure being prevented is silent.</b> <see cref="File.WriteAllText(string,string)"/>
/// TRUNCATES the target and then writes into it. Config stores typically load best-effort (corrupt ⇒
/// fall back to defaults), so an interrupted write does not fail loudly — it silently resets the user's
/// settings, and nobody notices until they wonder why their preferences reverted.
/// </para>
/// </summary>
public static class Files
{
    /// <summary>
    /// The suffix appended to the target path to form the temp file.
    /// <para>
    /// FIXED, not random: a crash before the rename leaves ONE predictable leftover that the next
    /// successful write overwrites, instead of debris nobody sweeps. ⚠ Two concurrent writers of the same
    /// path therefore SHARE it — pass your own to <see cref="BeginReplace(string,string)"/> for anything
    /// long-running.
    /// </para>
    /// </summary>
    public const string DefaultTempSuffix = ".tmp";

    /// <summary>
    /// The default text encoding: UTF-8 with NO byte-order mark — a BOM is a silent format change for a
    /// file other tools already parse. A default, not a rule: pass your own to
    /// <see cref="WriteAllText"/>.
    /// </summary>
    public static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Write text, replacing the file atomically. Throws on failure; the previous file survives.</summary>
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
    /// than buffering a whole file to hand to <see cref="WriteAllBytes"/>. <b>Throws on failure, leaving
    /// the previous file intact</b>; a best-effort caller catches and keeps the old file.
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
            // Still flushed: Direct gives up ATOMICITY, not durability.
            target.Flush(flushToDisk: true);
            return;
        }

        using var replacement = BeginReplace(path);
        using (var stream = new FileStream(replacement.TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            write(stream);
        }
        replacement.Commit();
    }

    /// <summary>
    /// Begin a replacement: get a temp path beside <paramref name="targetPath"/>, produce whatever you
    /// like into it — an encode, a compile, an extraction, a render — and
    /// <see cref="FileReplacement.Commit"/> when it is good. Disposing without committing discards it,
    /// so an interruption costs the WORK and never the original.
    /// <para><b>Verify before you commit.</b> "Finished writing" is not "valid" — a truncated encode is
    /// fully written and worthless, and swapping it in destroys the original. Only the caller knows what
    /// valid means for its format.
    /// </para>
    /// <para><b>Concurrency is the caller's.</b> Two transforms of one target sharing a temp suffix will
    /// collide: pass distinct suffixes, or hold a <see cref="MissionClaim"/> on the path through
    /// <see cref="IMissionScheduler"/>.
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
/// One in-flight atomic replacement — see <see cref="Files.BeginReplace(string,string)"/>. Produce into
/// <see cref="TempPath"/>, then <see cref="Commit"/>. Disposing without committing discards the temp, so
/// a <c>using</c> that throws cleans up on its way out.
/// </summary>
public sealed class FileReplacement : IDisposable
{
    private bool _committed;
    private bool _disposed;

    internal FileReplacement(string targetPath, string tempSuffix)
    {
        TargetPath = targetPath;
        TempPath = targetPath + tempSuffix;

        // The temp is a SIBLING of the target, never the system temp folder: a rename is atomic only
        // WITHIN a volume, and across volumes it silently degrades to copy-then-delete.
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

        // File.Move, not File.Replace: Move needs no backup path and does not care whether the target
        // already exists, so one call covers the first write and every later one.
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
    /// WITHOUT THIS THE WHOLE EXERCISE CAN STILL FAIL: the rename is a metadata operation that completes
    /// long before the data does, so a power loss can leave an intact rename pointing at an EMPTY file.
    /// Opening the finished file and flushing its handle also covers the case where something ELSE wrote
    /// it (an encoder, a compiler), which a flush on our own writer cannot.
    /// </para>
    /// <para>
    /// ⚠ <b>NOT COVERED BY A TEST</b> — deleting this call leaves all of <c>FilesTests</c> green, because
    /// durability against power loss cannot be asserted from a process that is still running. Do not
    /// "simplify" it away because nothing went red.
    /// </para>
    /// </summary>
    private static void FlushToDisk(string path)
    {
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        handle.Flush(flushToDisk: true);
    }
}
