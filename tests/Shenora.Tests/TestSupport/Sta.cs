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
/// Shared home for what used to be three byte-identical copies of this runner (P5.5 H7); new tests
/// should use it rather than adding a fourth.
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
        // Generous but bounded: a deadlock must fail the test, not hang the suite.
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The STA test body did not finish within 30s.");
        if (failure is not null) throw failure;
    }
}
