using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The main-window registry + modal-interaction blocking, ported from the primary desktop
/// sibling: native dialogs need the main window's handle for ownership (z-order), and the
/// window is disabled while one is up so the user can't re-enter the app mid-dialog.
/// Registered by <c>UseWindows</c>; the runner sets the main form automatically.
/// <para>
/// The portable half is <see cref="IUiInteraction"/> in <c>Shenora</c> (block/unblock, which any
/// host can implement); what remains here is the <see cref="Form"/>-typed part, which is Windows by
/// definition (D20). App logic that only needs to block the UI should depend on
/// <see cref="IUiInteraction"/> — <c>UseWindows</c> registers both faces of the same instance.
/// Note the blocking members are NOT redeclared here: re-declaring an inherited member is CS0108,
/// which is a build error now that warnings are errors.
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
/// The <see cref="IFormInteraction"/> implementation — the WinForms <c>Enabled</c> property does
/// the blocking (native modal feel, no fake overlay), with a nested count so overlapping dialogs
/// don't re-enable early.
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
        // A CREATED handle is a plain field read from any thread. The source marshalled via
        // Invoke here — but before the handle exists, touching Form.Handle CREATES it on the
        // calling thread (the wrong one, off the UI thread), so the fix is to answer Zero until
        // the UI thread has created it.
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
            // Marshalling goes through the ONE owner (P5.5 H4.2) — it holds the
            // IsHandleCreated-before-InvokeRequired rule, the non-blocking post, and the guarded
            // body in a single place. Non-blocking matters here specifically: this runs while
            // holding the block-count lock, and a blocking Invoke under a lock is the classic
            // pool-vs-UI deadlock (the family's measured AppHang shape). Posts are FIFO, so
            // block/unblock ordering is preserved.
            if (new WinFormsUiDispatcher(form).Post(() => form.Enabled = enabled)) return;

            // Not Ready: apply directly, deliberately. Control.Enabled before handle creation is
            // just a stored value — no handle to marshal to and nothing thread-affine about it — and
            // dropping it here would lose the block for a window that hasn't been shown yet.
            form.Enabled = enabled;
        }
        catch
        {
            // window tearing down mid-toggle — blocking a dying window is a no-op
        }
    }
}
