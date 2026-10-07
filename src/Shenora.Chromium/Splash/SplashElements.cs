using System.Drawing;

namespace Shenora.Chromium;

/// <summary>Where an element sits in the space its container gives it, on one axis.</summary>
public enum SplashAlign
{
    /// <summary>Left, or top.</summary>
    Start,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right, or bottom.</summary>
    End,

    /// <summary>Fills the space, unless the element sets its own size on that axis.</summary>
    Stretch,
}

/// <summary>The axis a <see cref="SplashStack"/> lays its children along.</summary>
public enum SplashOrientation
{
    /// <summary>Top to bottom.</summary>
    Vertical,

    /// <summary>Left to right.</summary>
    Horizontal,
}

/// <summary>Space around or inside an element, in device-independent pixels. A number converts to the same space on
/// every side.</summary>
/// <param name="Left">Left.</param>
/// <param name="Top">Top.</param>
/// <param name="Right">Right.</param>
/// <param name="Bottom">Bottom.</param>
public readonly record struct SplashInsets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The same space on every side.</summary>
    /// <param name="all">The space.</param>
    public SplashInsets(double all) : this(all, all, all, all) { }

    /// <summary>The same space on every side.</summary>
    /// <param name="all">The space.</param>
    public static implicit operator SplashInsets(double all) => new(all);
}

/// <summary>
/// One element of a splash: what a <see cref="SplashComponent"/>'s render function returns, as a tree. Sizes are
/// device-independent pixels, scaled to the display the splash is on. A <see cref="SplashStack"/> or
/// <see cref="SplashLayer"/> at the root fills the window; any other element sits in it by its own alignment.
/// </summary>
public abstract record SplashElement
{
    // Not for an app to derive from: the kit draws these five types, and an element of the app's own would lay out as
    // nothing. (A record's copy constructor stays protected, so this cannot be sealed off entirely.)
    private protected SplashElement() { }

    /// <summary>The width; null sizes it to its content (or to its container, when stretched).</summary>
    public double? Width { get; init; }

    /// <summary>The height; null sizes it to its content (or to its container, when stretched).</summary>
    public double? Height { get; init; }

    /// <summary>Space outside the element.</summary>
    public SplashInsets Margin { get; init; }

    /// <summary>Its place across the width its container gives it.</summary>
    public SplashAlign HorizontalAlign { get; init; } = SplashAlign.Center;

    /// <summary>Its place down the height its container gives it.</summary>
    public SplashAlign VerticalAlign { get; init; } = SplashAlign.Center;

    /// <summary>False hides it, and it takes no space.</summary>
    public bool Visible { get; init; } = true;
}

/// <summary>Children one after another, down or across, centred as a group by their container.</summary>
public sealed record SplashStack : SplashElement
{
    /// <summary>The axis the children follow.</summary>
    public SplashOrientation Orientation { get; init; }

    /// <summary>The space between two children.</summary>
    public double Spacing { get; init; }

    /// <summary>Space between the stack's edge and its children.</summary>
    public SplashInsets Padding { get; init; }

    /// <summary>A fill behind the children; null draws none.</summary>
    public Color? Background { get; init; }

    /// <summary>The children, in order.</summary>
    public IReadOnlyList<SplashElement> Children { get; init; } = [];
}

/// <summary>Children drawn over one another, each placed by its own alignment: a backdrop with content on it, or a
/// version label in a corner of a centred stack.</summary>
public sealed record SplashLayer : SplashElement
{
    /// <summary>Space between the layer's edge and its children.</summary>
    public SplashInsets Padding { get; init; }

    /// <summary>A fill behind the children; null draws none.</summary>
    public Color? Background { get; init; }

    /// <summary>The children, back to front.</summary>
    public IReadOnlyList<SplashElement> Children { get; init; } = [];
}

/// <summary>Text, wrapped at the width it is given, in the operating system's own text engine, so a script the font
/// lacks falls back as it does everywhere else on that system.</summary>
/// <param name="Text">The text.</param>
public sealed record SplashText(string Text) : SplashElement
{
    /// <summary>The font size, in device-independent pixels.</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>Bold.</summary>
    public bool Bold { get; init; }

    /// <summary>The colour; null picks one readable on the background behind it.</summary>
    public Color? Color { get; init; }

    /// <summary>A font family; null is the operating system's UI font.</summary>
    public string? FontFamily { get; init; }
}

/// <summary>A PNG image. A width or a height alone keeps its aspect ratio; neither draws it at its own size.</summary>
public sealed record SplashImage : SplashElement
{
    /// <summary>An image from a file.</summary>
    /// <param name="path">A PNG file; a relative path is relative to <see cref="AppContext.BaseDirectory"/>.</param>
    public SplashImage(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    /// <summary>An image from memory.</summary>
    /// <param name="png">The PNG's bytes.</param>
    public SplashImage(ReadOnlyMemory<byte> png) => Bytes = png;

    /// <summary>The file, when it came from one.</summary>
    public string? Path { get; }

    /// <summary>The bytes, when it came from memory.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }
}

/// <summary>A progress bar: <see cref="Value"/> filled, or, with none, a segment that slides while work of unknown
/// length runs. Its width is 280 and its height 4 unless it sets its own.</summary>
public sealed record SplashProgress : SplashElement
{
    /// <summary>How far along, from 0 to 1 (outside that is clamped); null is indeterminate.</summary>
    public double? Value { get; init; }

    /// <summary>The filled part's colour; null is the text colour for the background.</summary>
    public Color? Fill { get; init; }

    /// <summary>The unfilled part's colour; null is a faint blend of the fill over the background.</summary>
    public Color? Track { get; init; }
}
