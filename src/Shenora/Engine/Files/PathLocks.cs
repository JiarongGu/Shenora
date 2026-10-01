using Microsoft.Extensions.Logging;
using Shenora.Engine.Missions;
using Shenora.Core.Shell;

namespace Shenora.Engine.Files;

/// <summary>
/// A held cross-process lock on one path. Dispose to release; the OS releases it if the process dies.
/// </summary>
public interface IPathLease : IAsyncDisposable
{
    /// <summary>The canonical path this lease covers.</summary>
    string Path { get; }
}

/// <summary>
/// Cross-process exclusion for a path — the thing <see cref="MissionClaim"/> cannot do. A claim excludes
/// missions inside ONE scheduler in ONE process; this excludes any process that also takes a lease.
/// <para>
/// ⚠ <b>Advisory — it excludes PARTICIPANTS.</b> A process that never takes a lease (a game holding its
/// own assets open, antivirus, Explorer's thumbnailer) is completely unaffected; for those,
/// <see cref="RetryPolicy"/> + <see cref="IFileLockInspector"/> names the holder.
/// </para>
/// </summary>
public interface IPathLocker
{
    /// <summary>
    /// Take the lease, or return null if it is still held when <paramref name="timeout"/> expires.
    /// Null is a normal outcome, not an error: the caller DEFERS rather than forcing.
    /// </summary>
    /// <param name="path">The path to lock. Canonicalized, so two spellings are one lease.</param>
    /// <param name="timeout">How long to keep trying. <see cref="TimeSpan.Zero"/> = try once.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    Task<IPathLease?> TryAcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="FilePathLocker"/>.</summary>
public sealed class FilePathLockerOptions
{
    /// <summary>
    /// Directory the lock files live in. Required, and it should be the APP's own storage — never the
    /// tree being locked. ⚠ Two MACHINES sharing a tree need a directory ON THE SHARE: a lock file in
    /// one machine's local storage is invisible to the other, so everything works until both machines
    /// write the same file.
    /// </summary>
    public required string LockDirectory { get; init; }

    /// <summary>How often to re-attempt while waiting. Default 50ms.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Diagnostics sink, guarded through <see cref="AppCallback.Log"/>.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// Cross-process leases as lock FILES in a directory of the app's own, one per canonical path. Each
/// file holds the holder's process id and path, readable by <see cref="IFileLockInspector"/>.
/// <para>
/// The exclusion is the OS's rather than a convention, and it ends when the holding process exits — including
/// when it CRASHES, so a stale lock is never a state anyone has to clean up. On Windows it is the file's share
/// mode and the file is deleted on close; on Linux and macOS it is an exclusive <c>flock</c>, and the file stays
/// for the next acquirer — there, the file existing does not mean the path is held.
/// </para>
/// <para>
/// <b>Windows is the tested target.</b> ⚠ Over
/// an SMB2+ share, after a HARD failure (the holder crashes, the link drops) the server frees the handle
/// only when the SESSION TIMES OUT — a stale lease self-heals in tens of seconds, so size the lease
/// timeout for that.
/// </para>
/// </summary>
public sealed class FilePathLocker : IPathLocker
{
    private readonly FilePathLockerOptions _options;

    /// <param name="options">Where lock files live, and how often to retry.</param>
    public FilePathLocker(FilePathLockerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.LockDirectory);
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(FilePathLockerOptions.PollInterval)} must be positive — a zero poll spins a core.");
        _options = options;
        Directory.CreateDirectory(options.LockDirectory);
    }

    /// <inheritdoc/>
    public async Task<IPathLease?> TryAcquireAsync(
        string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var canonical = PathClaims.Canonical(path);
        var lockFile = LockFileFor(canonical);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Windows: the share mode is the exclusion (Read, so a diagnostic can still see WHO holds it) and the
                // file goes with its holder. Unix: .NET takes an flock that is EXCLUSIVE only for FileShare.None — Read
                // gave every writer a shared one, so two processes held one lease — and the file STAYS: unlinked on
                // release, a waiter that had already opened it locks the dead inode while a newcomer locks a new one.
                var unix = !OperatingSystem.IsWindows();
                var stream = new FileStream(lockFile, new FileStreamOptions
                {
                    Mode = unix ? FileMode.OpenOrCreate : FileMode.Create,
                    Access = FileAccess.Write,
                    Share = unix ? FileShare.None : FileShare.Read,
                    Options = unix ? FileOptions.None : FileOptions.DeleteOnClose,
                });
                if (unix) stream.SetLength(0);   // truncated only once held: OpenOrCreate leaves a holder's record alone
                await WriteHolderAsync(stream, canonical, cancellationToken).ConfigureAwait(false);
                Log(() => $"lease acquired: {canonical}");
                return new FileLease(canonical, stream, _options);
            }
            catch (IOException)
            {
                // Held by someone else — not an error.
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Log(() => $"lease NOT acquired within {timeout}: {canonical}");
                    return null;
                }
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The lock file for a path: a hash of it, in the app's own directory.</summary>
    private string LockFileFor(string canonicalPath)
    {
        var key = OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return System.IO.Path.Combine(_options.LockDirectory, $"{Convert.ToHexString(hash)[..24]}.lock");
    }

    private static async Task WriteHolderAsync(FileStream stream, string path, CancellationToken ct)
    {
        var holder = $"{Environment.ProcessId}\n{Environment.ProcessPath}\n{path}\n";
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(holder), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    private sealed class FileLease(string path, FileStream stream, FilePathLockerOptions options) : IPathLease
    {
        public string Path => path;

        public ValueTask DisposeAsync()
        {
            // DeleteOnClose removes the file; a failure here must not escape a using block.
            try { stream.Dispose(); }
            catch (Exception ex) { AppCallback.Log(options.Log, () => $"lease release failed for {path}: {ex.GetType().Name}"); }
            return ValueTask.CompletedTask;
        }
    }
}
