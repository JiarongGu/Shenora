using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// A small icon over a window's taskbar button, drawn from a text or a count: the shell's overlay icon
/// (<c>ITaskbarList3.SetOverlayIcon</c>).
/// <para>
/// 🔴 <b>An overlay belongs to a taskbar button, not to the window.</b> Explorer makes the button again when it restarts
/// and says so with the <c>TaskbarButtonCreated</c> message, and a recreated window handle gets a button of its own. So
/// the last text is kept and put back on each, and one shown before the button exists lands when the message says it
/// does. A shell with no taskbar shows none, and the window works as before.
/// </para>
/// <para>
/// On the window's own thread only, the one that made the shell's object. The icon is drawn at run time, at the
/// window's DPI.
/// </para>
/// </summary>
public sealed class TaskbarOverlay : IDisposable
{
    private readonly Form _form;
    private ButtonCreatedListener? _listener;
    private object? _taskbar;
    private bool _unavailable;
    private string? _text;
    private string? _description;
    private Color _fill = Color.FromArgb(0xC4, 0x2B, 0x1C);
    private Color _ink = Color.White;
    private IntPtr _icon;
    private bool _disposed;

    /// <summary>The message id Explorer sends a window once its taskbar button exists, and again after Explorer restarts.</summary>
    public static int ButtonCreatedMessage { get; } = (int)RegisterWindowMessage("TaskbarButtonCreated");

    /// <summary>Overlay <paramref name="form"/>'s button, from each handle it is given.</summary>
    public TaskbarOverlay(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        _form = form;
        form.HandleCreated += OnHandleCreated;
        form.HandleDestroyed += OnHandleDestroyed;
        if (form.IsHandleCreated) Listen();
    }

    /// <summary>The circle's colour. Default a red. Takes effect at once while an overlay is shown.</summary>
    public Color Fill
    {
        get => _fill;
        set { _fill = value; if (_text is not null) Apply(); }
    }

    /// <summary>The text's colour. Default white. Takes effect at once while an overlay is shown.</summary>
    public Color Ink
    {
        get => _ink;
        set { _ink = value; if (_text is not null) Apply(); }
    }

    /// <summary>The count past which <see cref="Show(int, string?)"/> says <c>99+</c> rather than the number.</summary>
    public const int MaxCount = 99;

    /// <summary>Show <paramref name="text"/> on the button, or clear it when null or empty.</summary>
    /// <param name="text">Short: it is shrunk to fit a 16-px circle, so a word is unreadable.</param>
    /// <param name="description">The screen reader's words. Null reads as the text.</param>
    public void Show(string? text, string? description = null)
    {
        _text = string.IsNullOrEmpty(text) ? null : text;
        _description = description;
        Apply();
    }

    /// <summary>Show <paramref name="count"/> on the button, <c>99+</c> past <see cref="MaxCount"/>, or clear it at none.</summary>
    /// <param name="count">What waits; zero or less clears the overlay.</param>
    /// <param name="description">The screen reader's words. Null reads as the number.</param>
    public void Show(int count, string? description = null) => Show(Label(count), description);

    /// <summary>Take the overlay off the button.</summary>
    public void Clear() => Show((string?)null);

    /// <summary>The text a count is drawn as: null at none, <c>99+</c> past <see cref="MaxCount"/>.</summary>
    internal static string? Label(int count) =>
        count <= 0 ? null : count > MaxCount ? MaxCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+"
        : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The icon's side at <paramref name="dpi"/>: 16 at 96, as the shell asks of an overlay.</summary>
    internal static int Side(int dpi) => Math.Max(16, (int)Math.Round(16 * dpi / 96.0));

    /// <summary>
    /// Replaces the shell call, which needs a taskbar: the window, the icon and the description it is given, answering an
    /// HRESULT. Tests set it; null calls the shell.
    /// </summary>
    internal Func<IntPtr, IntPtr, string?, int>? Surface { get; set; }

    /// <summary>
    /// Put the last text on the button as it is now: after its handle or its button was made again. The window forwards
    /// <see cref="ButtonCreatedMessage"/> here itself, so an app has no reason to call it.
    /// </summary>
    internal void Apply()
    {
        if (_disposed || !_form.IsHandleCreated || _form.IsDisposed) return;
        // Nothing to show and nothing of ours on a button: a new window is no reason to ask the shell for a taskbar.
        if (_text is null && _icon == IntPtr.Zero) return;
        var set = Surface ?? ShellSurface();
        if (set is null) return;

        IntPtr drawn;
        try
        {
            drawn = _text is null ? IntPtr.Zero : Draw(_text, Side(_form.DeviceDpi), _fill, _ink);
        }
        catch (Exception error) when (error is ExternalException or ArgumentException)
        {
            // A canvas that cannot be drawn on leaves the button as it is.
            return;
        }

        try
        {
            // An error before the button exists is the message's to mend, so the answer is not read.
            _ = set(_form.Handle, drawn, _text is null ? null : _description ?? _text);
        }
        catch (Exception error) when (error is ExternalException or InvalidCastException)
        {
            // A taskbar that cannot be told keeps what it shows.
            if (drawn != IntPtr.Zero) DestroyIcon(drawn);
            return;
        }

        // Ours to free, and only once another has replaced it on the button.
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = drawn;
    }

    /// <summary>
    /// <paramref name="text"/> in <paramref name="ink"/> on a circle of <paramref name="fill"/>, as an icon the caller frees.
    /// The text is an outline, centred by its own bounds and shrunk to fit the circle, so <c>99+</c> and <c>1</c> both sit
    /// in the middle rather than where the font's line box puts them.
    /// </summary>
    internal static IntPtr Draw(string text, int side, Color fill, Color ink)
    {
        using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.Clear(Color.Transparent);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            using (var circle = new SolidBrush(fill)) canvas.FillEllipse(circle, 0.5f, 0.5f, side - 1f, side - 1f);

            using var family = Family();
            using var outline = new GraphicsPath();
            outline.AddString(text, family, (int)FontStyle.Bold, side * 0.72f, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = outline.GetBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                var scale = Math.Min(1f, Math.Min(side * 0.66f / bounds.Width, side * 0.56f / bounds.Height));
                using var place = new Matrix();
                place.Translate(side / 2f, side / 2f);
                place.Scale(scale, scale);
                place.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
                outline.Transform(place);
                using var digits = new SolidBrush(ink);
                canvas.FillPath(digits, outline);
            }
        }

        return bitmap.GetHicon();
    }

    /// <summary>The system's face, or any sans-serif where it is missing.</summary>
    private static FontFamily Family()
    {
        try
        {
            return new FontFamily("Segoe UI");
        }
        catch (ArgumentException)
        {
            return new FontFamily(GenericFontFamilies.SansSerif);
        }
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        Listen();
        Apply();
    }

    private void OnHandleDestroyed(object? sender, EventArgs e) => _listener = null;

    // Subclassed beside the form's own window procedure, not through a hook the form offers: any Form takes it, and an app's own
    // message hook is left alone. The subclass ends with the handle.
    private void Listen()
    {
        if (_disposed || ButtonCreatedMessage == 0) return;
        _listener = new ButtonCreatedListener(this, _form.Handle);
    }

    /// <summary>Releases the overlay's icon and the shell's object. The button keeps the last icon until another replaces it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _form.HandleCreated -= OnHandleCreated;
        _form.HandleDestroyed -= OnHandleDestroyed;
        _listener?.Release();
        _listener = null;

        // Taken off the button first: an icon destroyed while the button still shows it is a blank square.
        if (_icon != IntPtr.Zero && _form.IsHandleCreated && !_form.IsDisposed)
        {
            try
            {
                _ = (Surface ?? ShellSurface())?.Invoke(_form.Handle, IntPtr.Zero, null);
            }
            catch (Exception error) when (error is ExternalException or InvalidCastException)
            {
                // A taskbar that cannot be told keeps what it shows.
            }
        }

        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        if (_taskbar is not null) Marshal.ReleaseComObject(_taskbar);
        _taskbar = null;
    }

    // The shell's taskbar object, made once on this thread; none for good once it would not be made.
    private Func<IntPtr, IntPtr, string?, int>? ShellSurface()
    {
        if (_unavailable) return null;
        if (_taskbar is null)
        {
            try
            {
                var taskbar = (ITaskbarList3)new TaskbarList();
                if (taskbar.HrInit() < 0)
                {
                    Marshal.ReleaseComObject(taskbar);
                    _unavailable = true;
                    return null;
                }

                _taskbar = taskbar;
            }
            catch (Exception error) when (error is ExternalException or InvalidCastException or UnauthorizedAccessException)
            {
                // A shell with no taskbar (a server's, a replacement shell's) has none to put an icon on.
                _unavailable = true;
                return null;
            }
        }

        var held = (ITaskbarList3)_taskbar;
        return (window, icon, description) => held.SetOverlayIcon(window, icon, description);
    }

    private sealed class ButtonCreatedListener : NativeWindow
    {
        private readonly TaskbarOverlay _owner;

        public ButtonCreatedListener(TaskbarOverlay owner, IntPtr handle)
        {
            _owner = owner;
            AssignHandle(handle);
        }

        public void Release()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ButtonCreatedMessage)
            {
                try
                {
                    _owner.Apply();
                }
                catch (Exception error) when (error is ExternalException or InvalidCastException or ObjectDisposedException)
                {
                    // The window keeps working: the message is the shell's, and a failed overlay never refuses it.
                }
            }

            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>The shell's taskbar list (<c>CLSID_TaskbarList</c>).</summary>
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    /// <summary>
    /// <c>ITaskbarList3</c> (<c>shobjidl_core.h</c>), with <c>ITaskbarList</c>'s and <c>ITaskbarList2</c>'s methods first in
    /// their order: the table is read by position, so each method up to the overlay is declared though only two are called.
    /// </summary>
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
        [PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr window, int flags);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr window);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr before);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr window, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr window, IntPtr images);
        [PreserveSig] int SetOverlayIcon(IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }
}
