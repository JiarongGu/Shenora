namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// Ready-made activity components — a complete, proportioned Live Activity in one call.
///
/// <para>
/// 🔴 <b>THIS IS THE KIT BEING A HELPER RATHER THAN A LIBRARY.</b> An adopter's activity is not usually a
/// design problem, it is the same three components over and over: work with a known end, work with an
/// unknown end, and a single number that matters. Each has metrics that are fiddly to get right and
/// invisible when wrong — where the value sits so it does not drift as the title changes, how much room
/// the bar needs, which slot survives the collapse into the pill, and where the sensor housing falls.
/// <b>Every one of those is settled here.</b>
/// </para>
///
/// <para>
/// ⚠ <b>They are FACTORIES, not a theme engine, and that is what keeps D13 intact.</b> Each returns an
/// ordinary <see cref="Presentation"/> built from the same public elements an app could have written
/// itself — no hidden rendering path, nothing the app cannot inspect or replace. Take one and override a
/// single surface with a <c>with</c> expression:
/// </para>
///
/// <code>
/// var layout = Components.ProgressCard("arrow.down.circle.fill") with
/// {
///     Minimal = new Icon("bolt.fill"),
/// };
/// </code>
///
/// <para>
/// ⚠ <b>No component names a TINT.</b> Every element inherits the layout-level colour from
/// <see cref="LiveActivityAppearance.Tint"/>, so one string re-themes the whole activity. A component that
/// baked in colours would be the design system the kit does not ship.
/// </para>
/// </summary>
public static class Components
{
    /// <summary>
    /// Work with a KNOWN end: a download, an export, a conversion. Icon and title say what it is, a bar
    /// says how far, a percentage says how much.
    /// <para>
    /// ⚠ Reads <c>{progress}</c>, so it is honest only when <see cref="LiveActivityState.Progress"/> is
    /// set — an indeterminate state renders the percentage EMPTY rather than as <c>0%</c>. Use
    /// <see cref="StatusCard"/> when the end is unknown.
    /// </para>
    /// </summary>
    /// <param name="symbol">An SF Symbol naming the work. ⚠ Filled symbols read on the Island; outlines do not.</param>
    public static Presentation ProgressCard(string symbol) => new()
    {
        LockScreen = Card(symbol, withBar: true, value: "{progress}"),
        Expanded = Panel(symbol, value: "{progress}", withBar: true),
        CompactLeading = new Icon(symbol),
        CompactTrailing = new Text("{progress}", TextRole.Value),
        Minimal = new Icon(symbol),
    };

    /// <summary>
    /// Work with an UNKNOWN end: syncing, waiting on a server, scanning. The same component without the
    /// percentage, because a number nobody can compute is worse than no number. The bar still renders —
    /// indeterminate, which says "running" without lying about how far along it is.
    /// </summary>
    /// <param name="symbol">An SF Symbol naming the work.</param>
    public static Presentation StatusCard(string symbol) => new()
    {
        LockScreen = Card(symbol, withBar: true, value: null),
        Expanded = Panel(symbol, value: null, withBar: true),
        CompactLeading = new Icon(symbol),
        // ⚠ The pill's trailing slot is left to the kit's own fallback, which draws a spinner when progress
        // is null. A layout putting the (empty) percentage here would reserve the space and render nothing,
        // which reads as a broken widget rather than as indeterminate work.
        Minimal = new Icon(symbol),
    };

    /// <summary>
    /// A single NUMBER that matters: a countdown, a score, a temperature, an ETA. No bar — the value IS
    /// the content, so it takes the trailing position on every surface and the subtitle carries the unit.
    /// </summary>
    /// <param name="symbol">An SF Symbol naming the subject.</param>
    /// <param name="value">
    /// What to show, as a binding or literal — <c>"{progress}"</c>, <c>"{subtitle}"</c>, or text the app
    /// updates through <see cref="LiveActivityState.Subtitle"/>.
    /// </param>
    public static Presentation CounterCard(string symbol, string value = "{subtitle}") => new()
    {
        LockScreen = Card(symbol, withBar: false, value: value),
        Expanded = Panel(symbol, value: value, withBar: false),
        CompactLeading = new Icon(symbol),
        CompactTrailing = new Text(value, TextRole.Value),
        Minimal = new Icon(symbol),
    };

    /// <summary>
    /// The lock-screen card: icon │ title over subtitle │ value, with a bar beneath.
    /// <para>
    /// 🔴 <see cref="Justify.SpaceBetween"/> is what makes this read as a CARD rather than a
    /// row of words — it pins the value to the trailing edge so the number lands in the same place
    /// whatever the title's length, instead of drifting on every update.
    /// </para>
    /// </summary>
    private static Element Card(string symbol, bool withBar, string? value)
    {
        List<Element> row =
        [
            new Icon(symbol),
            new Layout
            {
                Axis = Axis.Vertical,
                Spacing = 2,
                Children =
                [
                    new Text("{title}", TextRole.Headline),
                    new Text("{subtitle}", TextRole.Caption),
                ],
            },
        ];
        if (value is not null) row.Add(new Text(value, TextRole.Value));

        List<Element> card =
        [
            new Layout
            {
                Axis = Axis.Horizontal,
                Spacing = 12,
                Align = Align.Center,
                Justify = value is null ? Justify.Start : Justify.SpaceBetween,
                Children = row,
            },
        ];
        if (withBar) card.Add(new ProgressBar());

        // 16pt all round: the kit adds NO insets to a described region, so a card without these sits hard
        // against the banner's edges.
        return new Layout
        {
            Axis = Axis.Vertical,
            Spacing = 10,
            Align = Align.Fill,
            Insets = Insets.All(16),
            Children = card,
        };
    }

    /// <summary>
    /// The expanded panel as ONE layout, cutout and all — which is also the honest test of whether the
    /// container can express a real design rather than only a demo.
    /// <para>
    /// The icon, the cutout and the value form the row the housing splits; the kit renders what precedes
    /// the cutout in the Island's leading view and what follows it in the trailing view. Everything under
    /// the housing is described by the vertical layout the row sits inside.
    /// </para>
    /// </summary>
    private static Element Panel(string symbol, string? value, bool withBar)
    {
        List<Element> top =
        [
            new Icon(symbol),
            new Cutout(),
        ];
        if (value is not null) top.Add(new Text(value, TextRole.Value));

        List<Element> panel =
        [
            new Layout
            {
                Axis = Axis.Horizontal,
                Align = Align.Center,
                Justify = Justify.SpaceBetween,
                Children = top,
            },
            new Text("{title}", TextRole.Headline),
        ];
        if (withBar) panel.Add(new ProgressBar());
        panel.Add(new Text("{subtitle}", TextRole.Caption));

        return new Layout
        {
            Axis = Axis.Vertical,
            Spacing = 6,
            Align = Align.Fill,
            Insets = new Insets(Top: 2, Right: 0, Bottom: 6, Left: 0),
            Children = panel,
        };
    }
}
