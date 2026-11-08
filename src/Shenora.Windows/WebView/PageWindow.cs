namespace Shenora.Windows;

/// <summary>
/// The window of the page whose request is being handled: the top-level form showing it, which on a
/// <see cref="SecondaryWindows"/> window is that window and on its own thread, not the main window.
/// </summary>
/// <remarks>
/// Read it in a module's handler, or in what the handler starts, to act on "this window". It flows with the request as an
/// execution context does, so it is still there after an <c>await</c>. Null for a send from no page (a tray press, a launch,
/// a test), for a page whose control has since been collected, and for a page no form shows.
/// <para>⚠ The form belongs to its own thread: read its handle or ask it for state, and marshal anything else to that thread
/// (<see cref="WinFormsUiDispatcher"/>), which for a secondary window is not the main one.</para>
/// </remarks>
public static class PageWindow
{
    /// <summary>The form showing the page that sent the request being handled, or null as described above.</summary>
    public static Form? Current => PageSender.Current?.FindForm();
}
