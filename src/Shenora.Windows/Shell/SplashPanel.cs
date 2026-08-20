namespace Shenora.Windows;

/// <summary>Inputs for <see cref="SplashPanel"/>. Colors are the app's to choose (D13); match them to the
/// form's and the WebView2's background so startup is one continuous surface with no white flash.</summary>
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
/// The startup overlay shown over the WebView2 while the frontend boots — a centered marquee bar,
/// nothing else. Add it to the form ON TOP of the WebView2 control, then hide or remove it when the
/// frontend signals ready.
/// </summary>
public sealed class SplashPanel : Panel
{
    private readonly SplashPanelOptions _options;
    private readonly Panel _content;
    private readonly ProgressBar _bar;
    private readonly UiDebounce _resizeDebounce;

    /// <summary>A startup overlay. Colours come from <paramref name="options"/> — the kit ships none (D13).</summary>
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

        // Recenter on resize, debounced — drag-resize fires storms of layout passes otherwise.
        _resizeDebounce = new UiDebounce(50);
        Resize += (_, _) => _resizeDebounce.Execute(UpdateBarLayout);
        UpdateBarLayout();
    }

    /// <summary>
    /// Switch from the indeterminate marquee to a determinate bar at <paramref name="percent"/>
    /// (clamped 0–100). Marshals itself once the handle exists; before that, applies directly.
    /// ⚠ <c>IsHandleCreated</c> is checked FIRST because pre-handle <c>InvokeRequired</c> LIES — it
    /// reports false on a pool thread, and <c>BeginInvoke</c> then throws.
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) _resizeDebounce.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Trailing-edge debounce on the UI thread — a WinForms timer, so it ticks on the message loop and needs
/// no marshalling.
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
