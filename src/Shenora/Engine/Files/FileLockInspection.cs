namespace Shenora.Engine.Files;

// The lock-INSPECTION contract lives in the CORE layer, not beside the file engine, and the reason is the
// kit's oldest layering rule rather than convenience (D19/D20): "who is holding this file open?" has a
// genuinely DIFFERENT answer per platform — Windows asks the Restart Manager, and Linux/macOS/Android would
// each need their own — so it is a portable contract with a platform implementation, exactly like
// IFileDialogs and IPlaybackSession. Those contracts live in Core so a SHELL package can implement one
// without depending on the engine layer.
//
// Its sibling in the engine, IPathLocker, went the other way for the opposite reason: advisory lock files
// are portable, so contract and implementation ship together with the engine that uses them.
//
// ⚠ This comment said "Core, not Shenora.IO" and "an optional feature package" until 2026-08-09. Both
// referred to a package tier D55 abolished and a namespace D65 renamed to Shenora.Engine.Files — the
// SPLIT it describes is real and still load-bearing, but it is now a split between LAYERS of one package.
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
/// This is the half of the locking story that advisory locking (<see cref="IPathLocker"/>, in the
/// engine layer beside this contract's implementation) cannot cover. When the holder
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
