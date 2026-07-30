using System.Runtime.ExceptionServices;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// Runs a test body on a dedicated STA thread with a real message pump available.
/// <para>
/// xunit's workers are MTA, and WinForms handle creation that touches any OLE feature
/// (<c>AllowDrop</c>, the clipboard, drag-drop registration) throws INSIDE <c>WndProc</c> on an MTA
/// thread — which is not a clean test failure: WinForms pops a BLOCKING unhandled-exception dialog
/// and the whole suite stalls until someone dismisses it (found live; see
/// <c>.claude/rules/windows-dev-gotchas.md</c>). Anything that realizes a window handle in a test
/// belongs on this helper.
/// </para>
/// <para>
/// The ONE home for this runner. It replaced four copies (P5.5 H4.2 started it, H7 finished it) and
/// is the SUPERSET of what they each did, which is why nothing regressed on the way in: the copies
/// rethrew through <see cref="ExceptionDispatchInfo"/> (preserving the body's original stack trace —
/// a bare <c>throw failure;</c> resets it, so the failure pointed at this helper instead of the
/// assertion that failed), while only this one bounds the join. New tests use it rather than adding a
/// fifth copy.
/// </para>
/// </summary>
internal static class Sta
{
    /// <summary>Run <paramref name="body"/> on a fresh STA thread and rethrow anything it threw.</summary>
    public static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Generous but BOUNDED, and the three copies this replaced all used a bare Join(): a body that
        // deadlocks (trivial to write against a pump that never runs) hung the whole suite with no
        // failing test, which on CI is a job timeout with nothing to read.
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The STA test body did not finish within 30s.");
        // Capture/Throw, not `throw failure` — keep the body's stack trace pointing at the assertion.
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
