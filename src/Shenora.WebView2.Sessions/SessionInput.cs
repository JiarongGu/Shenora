using System.Text.Json;

namespace Shenora.WebView2.Sessions;

/// <summary>What a pointer input does to the streamed page.</summary>
public enum SessionPointerAction
{
    /// <summary>Cursor moved. A button held from an earlier <see cref="Down"/> carries through.</summary>
    Move,

    /// <summary>Primary button pressed (begins a click or a drag).</summary>
    Down,

    /// <summary>Primary button released.</summary>
    Up,
}

/// <summary>
/// One client input to replay into a co-browsed page.
/// <para>
/// This replaced <c>DispatchInputAsync(string json)</c>, which took the ORIGINATING APP'S WIRE
/// PROTOCOL as an opaque JSON string (P5.5 H9.1 / D21). That was "ship the consumer's shape" in its
/// purest form: a consumer could not know what to pass without reading that app's client, and the
/// framework's contract was one application's message format. The mechanics underneath are unchanged
/// and still the proven ones — only the seam is now typed. Use
/// <see cref="TryParseLegacyJson"/> to migrate an existing client mechanically.
/// </para>
/// <para>
/// COORDINATES ARE FRACTIONS of the viewport (0..1), not pixels, and that choice is load-bearing:
/// the client knows only the size of the image it is showing, so fractions are what make the protocol
/// resolution- and DPI-independent. The session maps them to CSS px using the viewport IT set, so no
/// round-trip to the page is needed.
/// </para>
/// <para>
/// The constructor is <c>private protected</c>, so the cases below are the intended whole set and
/// adding one is a deliberate act inside this package. Note this is NOT an airtight seal — a record's
/// compiler-generated COPY constructor is <c>protected</c>, so an outside type could still derive
/// through it — which is exactly why <see cref="StreamingSession.DispatchAsync"/> keeps an explicit
/// default arm rather than assuming its switch is exhaustive.
/// </para>
/// </summary>
public abstract record SessionInput
{
    private protected SessionInput() { }

    /// <summary>
    /// Parse one message in the pre-H9 wire format — the ADOPTION SHIM (D21's "accepted cost"), so an
    /// existing client that already speaks that protocol migrates without changing its frontend.
    /// Explicitly named "legacy" so nobody mistakes it for the contract: new callers construct the
    /// records directly.
    /// <para>
    /// Returns false for anything unrecognised or malformed rather than throwing — a single bad input
    /// message must never break a session, which was the old dispatcher's contract too.
    /// </para>
    /// </summary>
    /// <param name="json">
    /// <c>{"type":"viewport","width":…,"height":…,"dpr"?:…}</c> ·
    /// <c>{"type":"mouse","event":"pressed|released|moved","fx":…,"fy":…}</c> ·
    /// <c>{"type":"wheel","fx":…,"fy":…,"dy":…}</c> · <c>{"type":"text","text":…}</c> ·
    /// <c>{"type":"key","key":…,"alt"?,"ctrl"?,"meta"?,"shift"?}</c>
    /// </param>
    /// <param name="input">The parsed input, or null when this returns false.</param>
    public static bool TryParseLegacyJson(string? json, out SessionInput? input)
    {
        input = null;
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var typeElement)) return false;

            static double Num(JsonElement r, string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            static bool Flag(JsonElement r, string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            switch (typeElement.GetString())
            {
                case "viewport":
                    input = new SessionViewportInput(Num(root, "width"), Num(root, "height"))
                    {
                        DeviceScaleFactor = root.TryGetProperty("dpr", out var dpr)
                                            && dpr.ValueKind == JsonValueKind.Number
                            ? dpr.GetDouble()
                            : null,
                    };
                    return true;

                case "mouse":
                    // Anything that is not an explicit press/release is a MOVE — matching the old
                    // switch, whose default arm was mouseMoved.
                    input = new SessionPointerInput(
                        root.TryGetProperty("event", out var ev) ? ev.GetString() switch
                        {
                            "pressed" => SessionPointerAction.Down,
                            "released" => SessionPointerAction.Up,
                            _ => SessionPointerAction.Move,
                        } : SessionPointerAction.Move,
                        Num(root, "fx"), Num(root, "fy"));
                    return true;

                case "wheel":
                    input = new SessionWheelInput(Num(root, "fx"), Num(root, "fy"), Num(root, "dy"));
                    return true;

                case "text":
                    input = new SessionTextInput(
                        root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "");
                    return true;

                case "key":
                    var key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (key.Length == 0) return false;
                    input = new SessionKeyInput(key)
                    {
                        Alt = Flag(root, "alt"),
                        Ctrl = Flag(root, "ctrl"),
                        Meta = Flag(root, "meta"),
                        Shift = Flag(root, "shift"),
                    };
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Cursor movement or a primary-button press/release, at viewport FRACTIONS.
/// <para>
/// A button pressed by <see cref="SessionPointerAction.Down"/> stays held across subsequent
/// <see cref="SessionPointerAction.Move"/>s until <see cref="SessionPointerAction.Up"/> — the
/// session tracks that, because Chromium reads held state from the CDP <c>buttons</c> field and a
/// drag is impossible without it.
/// </para>
/// </summary>
/// <param name="Action">Move, press, or release.</param>
/// <param name="X">Horizontal position as a fraction of the viewport (0..1).</param>
/// <param name="Y">Vertical position as a fraction of the viewport (0..1).</param>
public sealed record SessionPointerInput(SessionPointerAction Action, double X, double Y) : SessionInput;

/// <summary>Scroll at a point, in CSS pixels of delta (negative scrolls up, the DOM convention).</summary>
/// <param name="X">Horizontal position as a fraction of the viewport (0..1).</param>
/// <param name="Y">Vertical position as a fraction of the viewport (0..1).</param>
/// <param name="DeltaY">Vertical scroll delta in CSS px.</param>
public sealed record SessionWheelInput(double X, double Y, double DeltaY) : SessionInput;

/// <summary>
/// Plain typed text, inserted as a unit (CDP <c>Input.insertText</c>) — the right primitive for
/// typing, including IME and pasted content. Use <see cref="SessionKeyInput"/> for keys that ACT
/// rather than type.
/// </summary>
/// <param name="Text">The text to insert.</param>
public sealed record SessionTextInput(string Text) : SessionInput;

/// <summary>
/// A key that acts rather than types — navigation/editing keys (arrows, Home/End, Delete, Enter) and
/// shortcuts (Ctrl/Meta combos). Sent as a real keyDown/keyUp pair with the modifier bitmask and the
/// Windows virtual-key code, which CDP needs for these to take effect at all.
/// </summary>
/// <param name="Key">The DOM key name, e.g. <c>"ArrowLeft"</c>, <c>"Enter"</c>, <c>"a"</c>.</param>
public sealed record SessionKeyInput(string Key) : SessionInput
{
    /// <summary>Alt held.</summary>
    public bool Alt { get; init; }

    /// <summary>Ctrl held.</summary>
    public bool Ctrl { get; init; }

    /// <summary>Meta (Windows/Command) held.</summary>
    public bool Meta { get; init; }

    /// <summary>Shift held.</summary>
    public bool Shift { get; init; }
}

/// <summary>
/// The client's content box, mirrored 1:1 into the page through device metrics ALONE — never a
/// physical resize (that mechanic is a kept primitive, see <see cref="StreamingSession"/>). Send this
/// whenever the viewer is resized; the session caches the result so pointer fractions need no
/// round-trip to the page.
/// </summary>
/// <param name="Width">Client content-box width in CSS px.</param>
/// <param name="Height">Client content-box height in CSS px.</param>
public sealed record SessionViewportInput(double Width, double Height) : SessionInput
{
    /// <summary>The client's device pixel ratio. Null uses the session default (1.5 — crisp text).</summary>
    public double? DeviceScaleFactor { get; init; }
}
