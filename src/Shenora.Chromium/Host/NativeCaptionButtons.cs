using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Shenora.Chromium.Interop;
using Shenora.Core.Ipc;

namespace Shenora.Chromium.Host;

/// <summary>
/// The caption buttons the window paints itself, over the rectangles the page reserved with
/// <c>SET_CAPTION_BUTTONS</c>: one Views overlay per button showing the platform's glyph, repainted from the same
/// <see cref="CaptionButtons"/> state the page is sent, on the thread that saw the message. An idle button is
/// transparent, so the page's own title bar shows through and there is no seam to match. CEF's UI thread.
/// </summary>
internal sealed unsafe class NativeCaptionButtons : IDisposable
{
    // The size Windows draws caption glyphs at, in DIPs.
    private const int GlyphDips = 10;

    /// <summary>A hover fades in over this and out over <see cref="FadeOut"/>: the system's own timings, measured on a
    /// real caption (about 85 ms and 150 ms). A press is instant.</summary>
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(85);

    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly _cef_window_t* _window;
    private readonly ButtonDelegate _delegate = new();
    private readonly Dictionary<CaptionButtonKind, Slot> _slots = [];
    // A glyph's coverage per pixel size (null: no icon font, remembered so it is not asked again), and one image
    // per glyph, colour and size. The colours are the palettes' own, since a fade swaps the glyph at its middle.
    private readonly Dictionary<(char Glyph, int Em), CaptionGlyphs.Mask?> _masks = [];
    private readonly Dictionary<(char Glyph, uint Color, int Em), nint> _images = [];
    private CaptionButtonPalette _palette;
    private CaptionButtonState _state = new(null, null);
    private double _scale = 1.0;
    private bool _maximized;
    private bool _active = true;
    private bool _ticking;
    private bool _disposed;

    private sealed class Slot
    {
        public _cef_label_button_t* Button;
        public _cef_overlay_controller_t* Overlay;
        public bool Visible;
        public nint Image;
        public char ImageGlyph;
        public uint Background = 1;   // never a real first value, so the first paint always sets it
        public CaptionButtonLook? Shown, From, Target;
        public bool TargetPressed;
        public TimeSpan FadeLength;
        public long FadeStart = -1;   // Environment.TickCount64 while fading
    }

    /// <param name="window">The window, whose reference its owner holds for longer than this lives.</param>
    /// <param name="palette">The colours.</param>
    public NativeCaptionButtons(_cef_window_t* window, CaptionButtonPalette palette)
    {
        _window = window;
        _palette = palette;
    }

    /// <summary>Put the buttons where the page reserved them, in DIPs; a kind the page left out is hidden.</summary>
    public void Place(IReadOnlyList<CaptionButtonRect> regions, double scale)
    {
        if (_disposed) return;
        _maximized = _window->is_maximized(_window) == 1;
        if (regions.Count > 0 && scale != _scale)
        {
            // A new DPI: every glyph is drawn again at its pixel size, idle ones included.
            _scale = scale;
            foreach (var existing in _slots.Values) existing.ImageGlyph = '\0';
        }
        foreach (var kind in Enum.GetValues<CaptionButtonKind>())
        {
            var region = regions.FirstOrDefault(r => r.Kind == kind);
            if (region.Width <= 0)
            {
                if (_slots.TryGetValue(kind, out var unused) && unused.Visible) { unused.Overlay->set_visible(unused.Overlay, 0); unused.Visible = false; }
                continue;
            }
            var slot = SlotFor(kind);
            var bounds = new _cef_rect_t
            {
                x = (int)Math.Round(region.X / scale), y = (int)Math.Round(region.Y / scale),
                width = (int)Math.Round(region.Width / scale), height = (int)Math.Round(region.Height / scale),
            };
            slot.Overlay->set_bounds(slot.Overlay, &bounds);
            if (!slot.Visible) { slot.Overlay->set_visible(slot.Overlay, 1); slot.Visible = true; }
            Paint(kind, slot);
        }
    }

    /// <summary>What the pointer is doing to the buttons.</summary>
    public void Show(CaptionButtonState state)
    {
        if (_disposed) return;
        _state = state;
        foreach (var (kind, slot) in _slots) Paint(kind, slot);
    }

    /// <summary>The window was maximized or restored: maximize's glyph is restore's while maximized.</summary>
    public void WindowSized()
    {
        if (_disposed) return;
        var maximized = _window->is_maximized(_window) == 1;
        if (maximized == _maximized) return;
        _maximized = maximized;
        if (_slots.TryGetValue(CaptionButtonKind.Maximize, out var slot)) Paint(CaptionButtonKind.Maximize, slot);
    }

    /// <summary>An inactive window's glyphs are dimmed, as the system's are.</summary>
    public void Activated(bool active)
    {
        if (_disposed || active == _active) return;
        _active = active;
        foreach (var (kind, slot) in _slots) Paint(kind, slot);
    }

    public void SetPalette(CaptionButtonPalette palette)
    {
        _palette = palette;
        foreach (var (kind, slot) in _slots) Paint(kind, slot);
    }

    private Slot SlotFor(CaptionButtonKind kind)
    {
        if (_slots.TryGetValue(kind, out var slot)) return slot;
        var empty = new _cef_string_utf16_t();
        var button = Cef.cef_label_button_create(_delegate.ForCef(), &empty);
        var view = &button->@base.@base;
        button->set_horizontal_alignment(button, cef_horizontal_alignment_t.CEF_HORIZONTAL_ALIGNMENT_CENTER);
        button->@base.set_ink_drop_enabled(&button->@base, 0);
        view->set_focusable(view, 0);
        // The overlay takes a reference of its own; ours is kept to recolour the button.
        view->@base.add_ref(&view->@base);
        var overlay = _window->add_overlay_view(_window, view, cef_docking_mode_t.CEF_DOCKING_MODE_CUSTOM, 0);
        slot = new Slot { Button = button, Overlay = overlay };
        _slots[kind] = slot;
        return slot;
    }

    // Towards the look this button's state asks for: a hover coming or going fades, anything pressed is instant.
    private void Paint(CaptionButtonKind kind, Slot slot)
    {
        var pressed = _state.Pressed == kind;
        var target = _palette.For(kind, hot: _state.Hot == kind, pressed, _active);
        var glyph = Glyph(kind, _maximized);
        if (slot.Target == target && slot.ImageGlyph == glyph) return;
        var fades = slot.Shown is not null && !pressed && !slot.TargetPressed && slot.Target != target;
        slot.From = slot.Shown;
        slot.Target = target;
        slot.TargetPressed = pressed;
        if (!fades)
        {
            slot.FadeStart = -1;
            Apply(kind, slot, target, 1.0);
            return;
        }
        slot.FadeStart = Environment.TickCount64;
        slot.FadeLength = _state.Hot == kind ? FadeIn : FadeOut;
        if (!_ticking) Tick();
    }

    private void Tick()
    {
        _ticking = false;
        if (_disposed) return;
        var now = Environment.TickCount64;
        foreach (var (kind, slot) in _slots)
        {
            if (slot.FadeStart < 0 || slot.From is not { } from || slot.Target is not { } target) continue;
            var t = Math.Min(1.0, (now - slot.FadeStart) / slot.FadeLength.TotalMilliseconds);
            Apply(kind, slot, CaptionButtonLook.Mix(from, target, t), t);
            if (t >= 1.0) slot.FadeStart = -1;
            else _ticking = true;
        }
        if (_ticking && !CefTask.PostDelayed(cef_thread_id_t.TID_UI, Frame, Tick)) _ticking = false;
    }

    private void Apply(CaptionButtonKind kind, Slot slot, CaptionButtonLook look, double t)
    {
        // The fill moves every frame; the glyph swaps at the middle, so its colours stay the palettes' own.
        var glyphColour = t >= 0.5 || slot.From is not { } from ? (slot.Target ?? look).Glyph : from.Glyph;
        slot.Shown = look with { Glyph = glyphColour };
        var view = &slot.Button->@base.@base;
        if (slot.Background != look.Background)
        {
            view->set_background_color(view, look.Background);
            slot.Background = look.Background;
        }
        var glyph = Glyph(kind, _maximized);
        var image = ImageFor(glyph, glyphColour);
        if (image != 0 && image != slot.Image)
        {
            // The call takes a reference; the cache keeps its own.
            ((_cef_base_ref_counted_t*)image)->add_ref((_cef_base_ref_counted_t*)image);
            slot.Button->set_image(slot.Button, cef_button_state_t.CEF_BUTTON_STATE_NORMAL, (_cef_image_t*)image);
            slot.Image = image;
        }
        slot.ImageGlyph = glyph;
    }

    // One image per glyph, colour and pixel size, drawn once. Views picks the representation for the display's scale.
    private nint ImageFor(char glyph, uint argb)
    {
        var em = Math.Max(1, (int)Math.Round(GlyphDips * _scale));
        if (_images.TryGetValue((glyph, argb, em), out var cached)) return cached;
        if (!_masks.TryGetValue((glyph, em), out var mask)) _masks[(glyph, em)] = mask = CaptionGlyphs.Rasterize(glyph, em);
        if (mask is null) return 0;

        // Premultiplied BGRA: the mask is the coverage, the colour its own alpha times it.
        var pixels = new uint[mask.Size * mask.Size];
        uint a = argb >> 24, r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        for (var i = 0; i < pixels.Length; i++)
        {
            var alpha = a * mask.Coverage[i] / 255;
            pixels[i] = (alpha << 24) | (r * alpha / 255 << 16) | (g * alpha / 255 << 8) | (b * alpha / 255);
        }
        var image = Cef.cef_image_create();
        fixed (uint* p = pixels)
            image->add_bitmap(image, (float)_scale, mask.Size, mask.Size, cef_color_type_t.CEF_COLOR_TYPE_BGRA_8888,
                cef_alpha_type_t.CEF_ALPHA_TYPE_PREMULTIPLIED, p, (nuint)(pixels.Length * sizeof(uint)));
        _images[(glyph, argb, em)] = (nint)image;
        return (nint)image;
    }

    /// <summary>The platform's glyphs, by code point: a private-use literal in the source is one bad encoding away from
    /// an empty button, and one editor away from being written raw.</summary>
    internal static char Glyph(CaptionButtonKind kind, bool maximized) => kind switch
    {
        CaptionButtonKind.Minimize => (char)0xE921,                              // ChromeMinimize
        CaptionButtonKind.Maximize => maximized ? (char)0xE923 : (char)0xE922,   // ChromeRestore / ChromeMaximize
        _ => (char)0xE8BB,                                                       // ChromeClose
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values)
        {
            if (slot.Overlay->is_valid(slot.Overlay) == 1) slot.Overlay->destroy(slot.Overlay);
            using (new CefRef<_cef_overlay_controller_t>(slot.Overlay)) { }
            using (new CefRef<_cef_label_button_t>(slot.Button)) { }
        }
        _slots.Clear();
        foreach (var image in _images.Values) using (new CefRef<_cef_image_t>((_cef_image_t*)image)) { }
        _images.Clear();
        _delegate.Release();
    }

    /// <summary>CEF requires a delegate for a button; the hit-test, not Views, decides what a press does.</summary>
    private sealed class ButtonDelegate : CefObject<_cef_button_delegate_t>
    {
        public ButtonDelegate() => Struct->on_button_pressed = &Pressed;

        [UnmanagedCallersOnly]
        private static void Pressed(_cef_button_delegate_t* self, _cef_button_t* button)
        {
            using var b = new CefRef<_cef_button_t>(button);
        }
    }
}

/// <summary>One button's colours in one state, as ARGB.</summary>
internal readonly record struct CaptionButtonLook(uint Background, uint Glyph)
{
    /// <summary>Part way from <paramref name="from"/> to <paramref name="to"/>, mixed premultiplied, so a fill fading in
    /// over the page keeps its hue and gains only opacity.</summary>
    public static CaptionButtonLook Mix(CaptionButtonLook from, CaptionButtonLook to, double t) =>
        new(MixArgb(from.Background, to.Background, t), MixArgb(from.Glyph, to.Glyph, t));

    private static uint MixArgb(uint from, uint to, double t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;
        double fa = (from >> 24) / 255.0, ta = (to >> 24) / 255.0;
        var a = fa + (ta - fa) * t;
        if (a <= 0) return 0;
        uint Channel(int shift)
        {
            var f = ((from >> shift) & 0xFF) * fa;
            var c = ((to >> shift) & 0xFF) * ta;
            return (uint)Math.Clamp(Math.Round((f + (c - f) * t) / a), 0, 255);
        }
        return ((uint)Math.Round(a * 255) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}

/// <summary>
/// The caption buttons' colours: the system's own, measured on a real Windows 11 caption (build 26200, a DefWindowProc
/// window, light and dark). Each fill is a white or black wash that reproduces the system's pixels over its own caption
/// to within one unit per channel, and so do the glyphs, the dark inactive one included; the light inactive glyph is
/// not measured. A pressed glyph keeps its colour, as the system's does. The tests pin each against its pixel.
/// </summary>
internal sealed record CaptionButtonPalette(
    uint Hover, uint Pressed, uint Glyph, uint InactiveGlyph, uint CloseHover, uint ClosePressed, uint CloseGlyphHot)
{
    public static readonly CaptionButtonPalette Dark = new(
        Hover: 0x0FFFFFFF, Pressed: 0x0BFFFFFF, Glyph: 0xFFFFFFFF, InactiveGlyph: 0x5AFFFFFF,
        CloseHover: 0xFFC42B1C, ClosePressed: 0xE6C42B1C, CloseGlyphHot: 0xFFFFFFFF);

    // The inactive glyph is not measured here.
    public static readonly CaptionButtonPalette Light = new(
        Hover: 0x0A000000, Pressed: 0x06000000, Glyph: 0xFF000000, InactiveGlyph: 0x5C000000,
        CloseHover: 0xFFC42B1C, ClosePressed: 0xE6C42B1C, CloseGlyphHot: 0xFFFFFFFF);

    public static CaptionButtonPalette ForTheme(bool dark) => dark ? Dark : Light;

    /// <summary>
    /// The page's own colours (<c>SET_CAPTION_BUTTON_COLORS</c>: <c>{ colors: { surface, hover, … } }</c>, CSS hex), or
    /// null when it sent none. <c>surface</c> is required as on the WebView2 shell and unused here, where an idle button
    /// is transparent. An inactive glyph not given is the glyph at the system's opacity for one.
    /// </summary>
    public static CaptionButtonPalette? FromPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("colors", out var c)
            || c.ValueKind != JsonValueKind.Object)
            return null;
        static uint Argb(Color color) => (uint)color.ToArgb();
        _ = PayloadHelper.GetRequiredColor(c, "surface");
        var glyph = PayloadHelper.GetRequiredColor(c, "glyph");
        return new(
            Hover: Argb(PayloadHelper.GetRequiredColor(c, "hover")),
            Pressed: Argb(PayloadHelper.GetRequiredColor(c, "pressed")),
            Glyph: Argb(glyph),
            InactiveGlyph: Argb(PayloadHelper.GetOptionalColor(c, "inactiveGlyph") ?? Color.FromArgb(glyph.A * 0x5A / 255, glyph)),
            CloseHover: Argb(PayloadHelper.GetRequiredColor(c, "closeHover")),
            ClosePressed: Argb(PayloadHelper.GetRequiredColor(c, "closePressed")),
            CloseGlyphHot: Argb(PayloadHelper.GetOptionalColor(c, "closeGlyphHot") ?? glyph));
    }

    /// <summary>The system's app theme (Settings → Personalization → Colors): light unless it says dark.</summary>
    public static CaptionButtonPalette SystemTheme() => ForTheme(global::Shenora.Chromium.Host.SystemTheme.IsDark() == true);

    /// <summary>The theme that reads on <paramref name="background"/>: dark (white glyphs) where its luminance is below
    /// half, else light; light with none, or one with no alpha, which CEF paints white. What a splash's strip sits on
    /// decides its glyphs, not the system's theme: a dark window on a light desktop drew black glyphs on black
    /// (measured, Linux).</summary>
    public static CaptionButtonPalette ForBackground(Color? background) =>
        background is { A: > 0 } c ? ForTheme(((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255 < 0.5) : Light;

    /// <summary>A splash's title strip (<see cref="SplashTitleBarOptions"/>): its colours over <paramref name="theme"/>,
    /// close kept the platform's red. An inactive glyph is the app's glyph at the system's opacity for one.</summary>
    public static CaptionButtonPalette ForStrip(SplashTitleBarOptions bar, CaptionButtonPalette theme)
    {
        static uint Argb(Color color) => (uint)color.ToArgb();
        return theme with
        {
            Glyph = bar.Glyph is { } glyph ? Argb(glyph) : theme.Glyph,
            InactiveGlyph = bar.Glyph is { } inactive ? Argb(Color.FromArgb(inactive.A * 0x5A / 255, inactive)) : theme.InactiveGlyph,
            Hover = bar.Hover is { } hover ? Argb(hover) : theme.Hover,
            Pressed = bar.Pressed is { } pressed ? Argb(pressed) : theme.Pressed,
        };
    }

    public CaptionButtonLook For(CaptionButtonKind kind, bool hot, bool pressed, bool active)
    {
        var close = kind == CaptionButtonKind.Close;
        if (pressed) return new(close ? ClosePressed : Pressed, close ? CloseGlyphHot : Glyph);
        if (hot) return new(close ? CloseHover : Hover, close ? CloseGlyphHot : Glyph);
        return new(0, active ? Glyph : InactiveGlyph);
    }
}
