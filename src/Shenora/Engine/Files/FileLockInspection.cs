namespace Shenora.Engine.Files;

// The lock-INSPECTION contract lives in Core, not in Shenora.IO, and the reason is the kit's oldest
// layering rule rather than convenience (D19/D20): "who is holding this file open?" has a genuinely
// DIFFERENT answer per platform — Windows asks the Restart Manager, and Linux/macOS/Android would each
// need their own — so it is a portable contract with a platform implementation, exactly like IFileDialogs
// and IPlaybackSession. Those contracts live in Core so a shell package can implement one without taking
// a dependency on an optional feature package.
//
// Its sibling in Shenora.IO, IPathLocker, went the other way for the opposite reason: advisory lock files
// are portable, so contract and implementation ship together with the engine that uses them.
/// <summary>A process holding a handle to a file, from <see cref="IFileLockInspector"/>.</summary>
/// <param name="ProcessId">OS process id.</param>
/// <param name="ProcessName">Executable name, or a best-effort description.</param>
public readonly record struct FileLockHolder(int ProcessId, string ProcessName)
{
    /// <inheritdoc/>
    public override string ToString() => $"{ProcessName} ({ProcessId})";
}

/// <summary>
/// Answers "who is holding this file?" — the question a bare <see cref="IOException"/> refuses to.
///
/// <para>
/// This is the half of the locking story that advisory locking (<c>IPathLocker</c>, in the optional
/// <c>Shenora.IO</c> package) cannot cover. When the holder
/// is a process that will never take a lease — a game with its assets open, antivirus, a shell
/// preview handler, another application editing a folder this app does not own — exclusion is
/// impossible and the only useful thing left is to say WHO, so the app can retry, ask the user to
/// close it, or report something better than "the process cannot access the file".
/// </para>
///
/// <para>
/// Implementations are platform-specific and live outside <c>Shenora</c>; this is the seam.
/// </para>
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
