namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// Ready-made activity components — a complete, proportioned Live Activity in one call. Each returns an
/// ordinary <see cref="Presentation"/> built from public elements, so a single surface can be overridden
/// with a <c>with</c> expression.
/// <para>
/// ⚠ <b>No component names a TINT.</b> Every element inherits the layout-level colour from
/// <see cref="LiveActivityAppearance.Tint"/>, so one string re-themes the whole activity.
/// </para>
/// </summary>
public static class Components
{
    /// <summary>
    /// Work with a KNOWN end: a download, an export, a conversion. Icon and title, a bar, a percentage.
    /// <para>
    /// ⚠ Reads <c>{progress}</c>: with <see cref="LiveActivityState.Progress"/> unset the percentage
    /// renders EMPTY rather than as <c>0%</c>. Use <see cref="StatusCard"/> when the end is unknown.
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
    /// Work with an UNKNOWN end: syncing, waiting on a server, scanning. <see cref="ProgressCard"/>
    /// without the percentage; the bar still renders, indeterminate.
    /// </summary>
    /// <param name="symbol">An SF Symbol naming the work.</param>
    public static Presentation StatusCard(string symbol) => new()
    {
        LockScreen = Card(symbol, withBar: true, value: null),
        Expanded = Panel(symbol, value: null, withBar: true),
        CompactLeading = new Icon(symbol),
        // ⚠ No CompactTrailing: the kit's fallback draws a spinner when progress is null. Putting the
        // (empty) percentage here reserves the space and renders nothing — a widget that reads as broken.
        Minimal = new Icon(symbol),
    };

    /// <summary>
    /// A single NUMBER that matters: a countdown, a score, a temperature, an ETA. No bar; the value takes
    /// the trailing position on every surface and the subtitle carries the unit.
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
    /// <see cref="Justify.SpaceBetween"/> pins the value to the trailing edge, so the number lands in the
    /// same place whatever the title's length instead of drifting on every update.
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

        // 16pt all round: the kit adds NO insets to a described region, so a card without these sits
        // hard against the banner's edges.
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
    /// The expanded panel as ONE layout, cutout and all. The icon, the cutout and the value form the row
    /// the housing splits: the kit renders what precedes the cutout in the Island's leading view and what
    /// follows it in the trailing view. Everything under the housing is described by the vertical layout
    /// the row sits inside.
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
