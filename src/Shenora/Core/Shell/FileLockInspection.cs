namespace Shenora.Core.Shell;

// The lock-INSPECTION contract lives in the CORE layer (D19/D20): "who is holding this file open?" has a
// genuinely DIFFERENT answer per platform, so it is a portable contract with a platform implementation,
// and Core is where a SHELL package can implement one without depending on the engine layer. Its sibling
// IPathLocker went the other way: advisory lock files are portable, so contract and implementation ship
// together with the engine that uses them.

/// <summary>A process holding a handle to a file, from <see cref="IFileLockInspector"/>.</summary>
/// <param name="ProcessId">OS process id.</param>
/// <param name="ProcessName">Executable name, or a best-effort description.</param>
public readonly record struct FileLockHolder(int ProcessId, string ProcessName)
{
    /// <inheritdoc/>
    public override string ToString() => $"{ProcessName} ({ProcessId})";
}

/// <summary>
/// Answers "who is holding this file?" — the question a bare <see cref="IOException"/> refuses to, and
/// the half of the locking story advisory locking (<c>IPathLocker</c>, in the engine layer) cannot cover.
/// When the holder will never take a lease — a game with its assets open, antivirus, a shell preview
/// handler — exclusion is impossible and the only useful thing left is to say WHO, so the app can retry,
/// ask the user to close it, or report something better than "the process cannot access the file".
/// Implementations are platform-specific and live outside <c>Shenora</c>; this is the seam.
/// </summary>
public interface IFileLockInspector
{
    /// <summary>
    /// Processes currently holding <paramref name="path"/> open. Empty when nothing holds it, when
    /// the platform cannot tell, or when the query itself fails — this is a diagnostic, so it never
    /// throws and never blocks a caller's real work.
    /// </summary>
    IReadOnlyList<FileLockHolder> WhoHolds(string path);
}
