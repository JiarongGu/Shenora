using System.Text.Json;

namespace Shenora.Chromium.Host;

/// <summary>Which caption button a page-drawn region stands in for. Serializes as the client's
/// <c>CaptionButtonKind</c>: <c>minimize</c>, <c>maximize</c>, <c>close</c>.</summary>
internal enum CaptionButtonKind
{
    Minimize,
    Maximize,
    Close,
}

/// <summary>One page-drawn caption button, in the top-level window's CLIENT pixels.</summary>
internal readonly record struct CaptionButtonRect(CaptionButtonKind Kind, int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>What the OS is doing to the caption buttons, as the page receives it. Null (omitted on the wire)
/// means no button.</summary>
internal sealed record CaptionButtonState(CaptionButtonKind? Hot, CaptionButtonKind? Pressed);

/// <summary>
/// One window's page-drawn caption buttons: where they are, which one the pointer is over or pressing, and
/// what a release does. Answering the hit-test with the button's code is what makes Windows 11 offer Snap
/// Layouts on maximize, and it costs the page every mouse event in those rectangles (Windows calls them
/// non-client), so hover, press and release are handled here, as <c>OptimizedForm</c> does for the WinForms
/// shell. Pure: the Win32 subclass (<see cref="CaptionHitTest"/>) feeds it, so it is tested without a window.
/// <para>
/// A press behaves as a native caption button's, which captures the mouse until the release: while it is held,
/// only its button shows, pressed while the pointer is on it and plain while it is off, and the release ends it
/// wherever it happens. Without that, a release on the drag bar left the button pressed (measured, real input).
/// </para>
/// </summary>
/// <param name="changed">The state changed. Called on the thread that fed the change.</param>
/// <param name="invoke">A button was clicked: pressed and released on the same button.</param>
internal sealed class CaptionButtons(Action<CaptionButtonState> changed, Action<CaptionButtonKind> invoke)
{
    private CaptionButtonRect[] _regions = [];
    private CaptionButtonKind? _hot;
    private CaptionButtonKind? _pressed;
    private CaptionButtonKind? _held;   // the button a press started on, until its release

    public bool IsEmpty => _regions.Length == 0;

    /// <summary>A press is held: the pointer's moves and the release belong to it.</summary>
    public bool IsPressed => _held is not null;

    /// <summary>Replace the regions. Clearing them clears the state too, a held press included, or the page is left
    /// rendering a hover that can never end.</summary>
    public void Set(IReadOnlyList<CaptionButtonRect> regions)
    {
        _regions = [.. regions];
        if (_regions.Length == 0) Cancel();
    }

    /// <summary>The button at a point in client pixels, or null.</summary>
    public CaptionButtonKind? At(int x, int y)
    {
        foreach (var region in _regions)
            if (region.Contains(x, y)) return region.Kind;
        return null;
    }

    /// <summary>The pointer moved onto <paramref name="kind"/>, or off every button.</summary>
    public void Hover(CaptionButtonKind? kind)
    {
        if (_held is { } held) Update(kind == held ? held : null, kind == held ? held : null);
        else Update(kind, null);
    }

    /// <summary>The pointer left the non-client area entirely, including into the page. A held press ignores it:
    /// its moves arrive through the capture.</summary>
    public void Leave()
    {
        if (_held is null) Update(null, null);
    }

    public void Press(CaptionButtonKind kind)
    {
        _held = kind;
        Update(kind, kind);
    }

    /// <summary>The press ended with the pointer on <paramref name="kind"/>, or off every button. Acts only if the
    /// press STARTED on that button, matching every other button on the system.</summary>
    public void Release(CaptionButtonKind? kind)
    {
        var held = _held;
        _held = null;
        Update(kind, null);
        if (kind is { } released && held == released) invoke(released);
    }

    /// <summary>The press was taken away (its capture lost): nothing shows, and nothing is clicked.</summary>
    public void Cancel()
    {
        _held = null;
        Update(null, null);
    }

    private void Update(CaptionButtonKind? hot, CaptionButtonKind? pressed)
    {
        if (_hot == hot && _pressed == pressed) return;
        _hot = hot;
        _pressed = pressed;
        changed(new CaptionButtonState(hot, pressed));
    }

    /// <summary>
    /// <c>SET_CAPTION_BUTTONS</c>'s <c>{ buttons: [{ kind, x, y, width, height }] }</c>, in CSS px relative to the
    /// page, into client pixels. The page fills the window's client area (measured: both origins at the same
    /// screen point), so the conversion is the scale alone. A malformed entry or a zero-size rectangle is
    /// SKIPPED rather than failing the call, as the WebView2 module does: rejecting a batch over one odd entry
    /// would drop the other buttons' hit-tests.
    /// </summary>
    /// <param name="payload">The request's payload.</param>
    /// <param name="scale">Physical pixels per CSS pixel: the window's DPI over 96.</param>
    public static IReadOnlyList<CaptionButtonRect> Parse(JsonElement? payload, double scale)
    {
        var regions = new List<CaptionButtonRect>(3);
        if (payload is not { ValueKind: JsonValueKind.Object } root) return regions;
        if (!root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return regions;

        foreach (var entry in buttons.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Kind(entry) is not { } kind) continue;
            var width = Px(entry, "width", scale);
            var height = Px(entry, "height", scale);
            if (width <= 0 || height <= 0) continue;
            regions.Add(new CaptionButtonRect(kind, Px(entry, "x", scale), Px(entry, "y", scale), width, height));
        }
        return regions;
    }

    private static int Px(JsonElement entry, string name, double scale) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble() * scale) : 0;

    private static CaptionButtonKind? Kind(JsonElement entry) =>
        entry.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()?.ToUpperInvariant() switch
            {
                "MINIMIZE" => CaptionButtonKind.Minimize,
                "MAXIMIZE" => CaptionButtonKind.Maximize,
                "CLOSE" => CaptionButtonKind.Close,
                _ => null,
            }
            : null;
}
