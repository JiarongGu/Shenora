namespace Shenora.Chromium.Host;

/// <summary>
/// A press on the title strip a splash draws itself (Linux, where the kit paints no caption buttons on the window): a
/// drag once the pointer moves past <see cref="SlopPx"/> while held, which the window manager then carries out; the
/// second of two presses within <see cref="DoubleClickMs"/> and the slop, which maximizes or restores; or nothing, for
/// a plain click. Pure; the splash's thread feeds it.
/// </summary>
internal sealed class StripGesture
{
    /// <summary>What the press asks of the window.</summary>
    internal enum Act
    {
        None,
        Drag,
        ToggleMaximize,
    }

    /// <summary>GTK's default double-click time, in ms.</summary>
    public const long DoubleClickMs = 400;

    /// <summary>How far, in pixels, a press may move before it is a drag, and a second press may land from the first.</summary>
    public const int SlopPx = 4;

    private bool _held, _dragged;
    private int _x, _y;
    private long _lastClickAt = long.MinValue;
    private int _lastX, _lastY;

    /// <summary>Where the press that became a drag was made, in the coordinates <see cref="Down"/> was given.</summary>
    public (int X, int Y) DragFrom => (_x, _y);

    public Act Down(int x, int y, long atMs)
    {
        // A second press that reads as earlier (X's 32-bit clock wrapped, every 49.7 days) is no double-click.
        var elapsed = _lastClickAt == long.MinValue ? -1 : atMs - _lastClickAt;
        var second = elapsed >= 0 && elapsed <= DoubleClickMs && Math.Abs(x - _lastX) <= SlopPx && Math.Abs(y - _lastY) <= SlopPx;
        _held = true;
        _dragged = false;
        _x = x;
        _y = y;
        if (second)
        {
            _lastClickAt = long.MinValue;   // a third press starts afresh
            return Act.ToggleMaximize;
        }
        _lastClickAt = atMs;
        _lastX = x;
        _lastY = y;
        return Act.None;
    }

    public Act Move(int x, int y)
    {
        if (!_held || _dragged) return Act.None;
        if (Math.Abs(x - _x) <= SlopPx && Math.Abs(y - _y) <= SlopPx) return Act.None;
        _dragged = true;
        _lastClickAt = long.MinValue;   // a press that dragged is no half of a double-click
        return Act.Drag;
    }

    public void Up() => _held = false;
}
