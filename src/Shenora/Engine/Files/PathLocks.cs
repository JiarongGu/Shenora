using Shenora.Engine.Missions;


using Shenora;

namespace Shenora.Engine.Files;

/// <summary>
/// A held cross-process lock on one path. Dispose to release; the OS releases it anyway if the
/// process dies, which is what keeps a crash from leaving a permanent lock behind.
/// </summary>
public interface IPathLease : IAsyncDisposable
{
    /// <summary>The canonical path this lease covers.</summary>
    string Path { get; }
}

/// <summary>
/// Cross-process exclusion for a path — the thing <see cref="MissionClaim"/> cannot do.
///
/// <para>
/// A claim excludes missions inside ONE scheduler in ONE process. This excludes any process that also
/// takes a lease: a second instance of the app, or a child process the app spawns and waits on. The
/// parent takes the lease for the duration of the child's work, which is what makes an external
/// command-line tool participate without knowing anything about this.
/// </para>
///
/// <para>
/// <b>Advisory, and the limit is the whole point.</b> It excludes PARTICIPANTS. A process that never
/// takes a lease — a game holding its own assets open, antivirus, Explorer's thumbnailer, another
/// application editing a folder this app does not own — is completely unaffected, and no lock design
/// can change that. For those, the answer is <see cref="RetryPolicy"/> plus
/// <see cref="IFileLockInspector"/> to name who is holding the handle.
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
    /// tree being locked.
    ///
    /// <para>
    /// Sidecar locks next to the target are the obvious design and the wrong one here: an app that
    /// manages a folder it does not OWN would be scattering files into a tree that other applications
    /// and the user are also editing, where they look like content, get synced, get committed, and
    /// outlive the process that made them.
    /// </para>
    ///
    /// <para>
    /// <b>Choose it by WHO is contending.</b> Several processes on one machine — the app and the
    /// tools it spawns, or a second instance — want the app's own local storage. Two MACHINES sharing
    /// a tree over a network share want a directory ON THE SHARE, because a lock file in one
    /// machine's local storage is invisible to the other. That is the setting an app gets wrong
    /// silently: everything works until two machines write the same file.
    /// </para>
    /// </summary>
    public required string LockDirectory { get; init; }

    /// <summary>How often to re-attempt while waiting. Default 50ms.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Diagnostics sink, guarded through <see cref="AppCallback.Log"/>.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Cross-process leases as lock FILES in a directory of the app's own, one per canonical path.
///
/// <para>
/// The lock file is opened <c>FileShare.Read</c> + <c>DeleteOnClose</c>. Two consequences, both
/// deliberate: a second holder cannot open it for writing, so the exclusion is the OS's rather than a
/// convention; and the file vanishes when the holding process exits — including when it CRASHES —
/// so a stale lock is not a state anyone has to clean up. A lock protocol whose failure mode is "now
/// nothing can ever run again until someone deletes a file" is worse than no lock protocol.
/// </para>
///
/// <para>
/// It writes the holder's process id and path into the file, readable by
/// <see cref="IFileLockInspector"/> or by a human, because "who has it?" is the first question when
/// something waits.
/// </para>
///
/// <para>
/// <b>Windows is the tested target.</b> The pattern works on POSIX, where file locking is advisory in
/// a different way and <c>DeleteOnClose</c> has no direct equivalent — claiming support that is not
/// tested would be worse than naming the limit.
/// </para>
///
/// <para>
/// <b>Over a network share it still works, with one caveat worth knowing.</b> SMB2+ carries the
/// delete-on-close flag, so the exclusion holds between machines — provided
/// <see cref="FilePathLockerOptions.LockDirectory"/> is ON the share, since a lock file in one
/// machine's local storage is invisible to the other. The caveat is release after a HARD failure: if
/// the holding machine crashes or the link drops, the server frees the handle when the session times
/// out rather than instantly, so a stale lease self-heals in tens of seconds rather than never. Set a
/// lease timeout that tolerates that.
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
                var stream = new FileStream(lockFile, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    // Read, not None: a diagnostic (or a human) can still see WHO holds it.
                    Share = FileShare.Read,
                    Options = FileOptions.DeleteOnClose,
                });
                await WriteHolderAsync(stream, canonical, cancellationToken).ConfigureAwait(false);
                Log(() => $"lease acquired: {canonical}");
                return new FileLease(canonical, stream, _options);
            }
            catch (IOException)
            {
                // Held by someone else. Not an error — wait, or tell the caller to defer.
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Log(() => $"lease NOT acquired within {timeout}: {canonical}");
                    return null;
                }
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The lock file for a path: a hash, in the app's own directory. A hash rather than a mangled
    /// path because a path is longer than a filename may be, and because the mangling would leak the
    /// managed tree's layout into the app's data folder.
    /// </summary>
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

    private void Log(Func<string> message) => AppCallback.Log(_options.Log, message);

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
