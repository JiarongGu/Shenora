using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The main-window registry + modal-interaction blocking: native dialogs need the main window's handle
/// for ownership (z-order), and the window is disabled while one is up. Registered by <c>UseWindows</c>;
/// the runner sets the main form automatically.
/// <para>
/// The portable half is <see cref="IUiInteraction"/> in <c>Shenora</c>, which app logic that only blocks
/// the UI should depend on (D20); what remains here is the <see cref="Form"/>-typed part.
/// ⚠ The blocking members are not redeclared — re-declaring an inherited member is CS0108, an error
/// under warnings-as-errors.
/// </para>
/// </summary>
public interface IFormInteraction : IUiInteraction
{
    /// <summary>Register the main window (the WinForms runner does this after the form factory).</summary>
    void SetMainForm(Form form);

    /// <summary>The main window, or null before the runner registers it.</summary>
    Form? GetMainForm();

    /// <summary>
    /// The main window's handle for dialog ownership, or <see cref="IntPtr.Zero"/> when there is
    /// no window (or no handle yet) — callers fall back to an unowned dialog.
    /// </summary>
    IntPtr GetMainFormHandle();
}

/// <summary>
/// The <see cref="IFormInteraction"/> implementation — the WinForms <c>Enabled</c> property does the
/// blocking, with a nested count so overlapping dialogs do not re-enable early.
/// </summary>
public sealed class FormInteraction : IFormInteraction
{
    private readonly object _lock = new();
    private Form? _mainForm;
    private int _blockCount;

    /// <inheritdoc />
    public void SetMainForm(Form form) => _mainForm = form ?? throw new ArgumentNullException(nameof(form));

    /// <inheritdoc />
    public Form? GetMainForm() => _mainForm;

    /// <inheritdoc />
    public IntPtr GetMainFormHandle()
    {
        // A CREATED handle is a plain field read from any thread. ⚠ But before the handle exists,
        // touching Form.Handle CREATES it on the CALLING thread — so answer Zero until the UI thread has.
        var form = _mainForm;
        return form is { IsDisposed: false, IsHandleCreated: true } ? form.Handle : IntPtr.Zero;
    }

    /// <inheritdoc />
    public void BlockInteraction()
    {
        lock (_lock)
        {
            _blockCount++;
            if (_blockCount == 1) SetEnabled(false);
        }
    }

    /// <inheritdoc />
    public void UnblockInteraction()
    {
        lock (_lock)
        {
            _blockCount = Math.Max(0, _blockCount - 1);
            if (_blockCount == 0) SetEnabled(true);
        }
    }

    private void SetEnabled(bool enabled)
    {
        var form = _mainForm;
        if (form is null || form.IsDisposed) return;
        try
        {
            // ⚠ NON-BLOCKING matters here specifically: this runs while holding the block-count lock, and
            // a blocking Invoke under a lock is the classic pool-vs-UI deadlock. Posts are FIFO, so
            // block/unblock ordering is preserved.
            if (new WinFormsUiDispatcher(form).Post(() => form.Enabled = enabled)) return;

            // Not Ready: apply directly. Control.Enabled before handle creation is just a stored value,
            // and dropping it would lose the block for a window that has not been shown yet.
            form.Enabled = enabled;
        }
        catch
        {
            // window tearing down mid-toggle — blocking a dying window is a no-op
        }
    }
}
