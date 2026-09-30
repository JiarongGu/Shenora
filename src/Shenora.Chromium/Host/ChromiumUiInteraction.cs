using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Chromium shell's <see cref="IUiInteraction"/>: the main window takes no input while something modal runs, as
/// the WinForms shell disables its main form. Nested, so overlapping blocks do not give input back early.
/// </summary>
internal sealed class ChromiumUiInteraction(ChromiumWindows windows) : IUiInteraction
{
    private readonly Lock _lock = new();
    private int _blocks;

    public void BlockInteraction()
    {
        // Under the lock, and the window's change is a POST: in the order asked, and never waiting on the UI thread.
        lock (_lock)
        {
            if (++_blocks == 1) windows.SetMainEnabled(false);
        }
    }

    public void UnblockInteraction()
    {
        lock (_lock)
        {
            if (_blocks == 0) return;
            if (--_blocks == 0) windows.SetMainEnabled(true);
        }
    }
}
