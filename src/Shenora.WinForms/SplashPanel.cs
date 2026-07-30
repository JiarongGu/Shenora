namespace Shenora.WinForms;

/// <summary>Inputs for <see cref="SplashPanel"/>. Colors are the app's to choose (the library is
/// headless — no design-system palette ships here); match them to the form's and the WebView2's
/// background so startup is one continuous surface (the family's no-white-flash contract).</summary>
public sealed class SplashPanelOptions
{
    /// <summary>Panel background. Neutral dark default — override to match the app.</summary>
    public Color BackColor { get; init; } = Color.FromArgb(31, 31, 31);

    /// <summary>Progress-bar accent. Best-effort: the OS ignores it while visual styles render
    /// the marquee. Neutral default — override to match the app.</summary>
    public Color BarColor { get; init; } = Color.FromArgb(102, 102, 102);

    /// <summary>Maximum bar width in px (below it, <see cref="BarWidthFraction"/> applies).</summary>
    public int BarMaxWidth { get; init; } = 400;

    /// <summary>Bar height in px.</summary>
    public int BarHeight { get; init; } = 4;

    /// <summary>Bar width as a fraction of the panel width, capped at <see cref="BarMaxWidth"/>.</summary>
    public double BarWidthFraction { get; init; } = 0.7;
}

/// <summary>
/// The startup overlay shown over the WebView2 while the frontend boots (WebView2 init + first
/// script compile is seconds on cold starts): a minimal centered marquee bar, nothing else.
/// Ported from the family app minus its dead status labels. Add it to the form ON TOP of the
/// WebView2 control, then hide/remove it when the frontend signals ready.
/// </summary>
public sealed class SplashPanel : Panel
{
    private readonly SplashPanelOptions _options;
    private readonly Panel _content;
    private readonly ProgressBar _bar;
    private readonly UiDebounce _resizeDebounce;

    public SplashPanel(SplashPanelOptions? options = null)
    {
        _options = options ?? new SplashPanelOptions();

        Dock = DockStyle.Fill;
        BackColor = _options.BackColor;
        DoubleBuffered = true;

        _bar = new ProgressBar
        {
            Location = new Point(0, 0),
            Size = new Size(_options.BarMaxWidth, _options.BarHeight),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 20,
            ForeColor = _options.BarColor,
        };
        _content = new Panel
        {
            Size = _bar.Size,
            BackColor = Color.Transparent,
        };
        _content.Controls.Add(_bar);
        Controls.Add(_content);

        // Recenter on resize, debounced — drag-resize fires storms of layout passes otherwise
        // (the source app's measured fix).
        _resizeDebounce = new UiDebounce(50);
        Resize += (_, _) => _resizeDebounce.Execute(UpdateBarLayout);
        UpdateBarLayout();
    }

    /// <summary>
    /// Switch from the indeterminate marquee to a determinate bar at <paramref name="percent"/>
    /// (clamped 0–100). Marshals itself once the handle exists; before that, applies directly
    /// (pre-handle <c>InvokeRequired</c> LIES — false on a pool thread — and <c>BeginInvoke</c>
    /// throws, the same trap the WebView2 deferral marshal guards against).
    /// <para>
    /// DELIBERATELY NOT routed through <see cref="WinFormsUiDispatcher"/>, unlike the other marshal
    /// sites collapsed in P5.5 H4.2 — and this is a judgement, not an oversight. Those were services
    /// marshalling to a FOREIGN control from an arbitrary thread, where centralising the
    /// handle/thread/guard decision removes duplicated risk. This is a control marshalling to
    /// ITSELF: the two-line self-post is idiomatic, its pre-handle "apply directly" is correct
    /// (setting a property on an unrealized control needs no marshal), and injecting a dispatcher
    /// would add a field and a construction site for zero correctness gain.
    /// </para>
    /// </summary>
    public void UpdateProgress(int percent)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(() => UpdateProgress(percent));
            return;
        }
        if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = Math.Clamp(percent, 0, 100);
    }

    /// <summary>Restyle after the app resolves its theme (the frontend usually knows the user's
    /// theme only once it has booted). Marshals itself once the handle exists (see
    /// <see cref="UpdateProgress"/> for the pre-handle rule).</summary>
    public void SetColors(Color backColor, Color barColor)
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(() => SetColors(backColor, barColor));
            return;
        }
        BackColor = backColor;
        _bar.ForeColor = barColor;
        Invalidate();
    }

    // Named test accessors for the two child controls (P5.5 H7). The tests used to reach them as
    // `Controls[0]` and `Controls[0].Controls[0]`, which asserted the CONTROL TREE SHAPE as a
    // side effect: inserting any decorative child, or reparenting the bar, would have failed a
    // layout test with an IndexOutOfRange or an InvalidCast rather than a message about layout.
    /// <summary>The centered container holding the bar. Internal for tests.</summary>
    internal Panel ContentPanel => _content;

    /// <summary>The progress bar itself. Internal for tests.</summary>
    internal ProgressBar Bar => _bar;

    /// <summary>Size the bar per options and center it. Internal for tests.</summary>
    internal void UpdateBarLayout()
    {
        var width = Math.Min((int)(Width * _options.BarWidthFraction), _options.BarMaxWidth);
        _content.Size = new Size(width, _options.BarHeight);
        _bar.Size = _content.Size;
        _content.Location = new Point((Width - _content.Width) / 2, (Height - _content.Height) / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _resizeDebounce.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Trailing-edge debounce on the UI thread (WinForms timer — ticks on the message loop, no
/// marshalling needed). Internal until the utilities extraction phase promotes a public set.
/// </summary>
internal sealed class UiDebounce(int delayMilliseconds) : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = delayMilliseconds };
    private Action? _pending;
    private bool _wired;

    /// <summary>Schedule <paramref name="action"/>, replacing any not-yet-fired one.</summary>
    public void Execute(Action action)
    {
        _pending = action;
        if (!_wired)
        {
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                var action = _pending;
                _pending = null;
                action?.Invoke();
            };
            _wired = true;
        }
        _timer.Stop();
        _timer.Start();
    }

    public void Dispose() => _timer.Dispose();
}
