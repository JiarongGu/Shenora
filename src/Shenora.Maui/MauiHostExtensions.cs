using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Dispatching;
using Shenora.Core;

namespace Shenora.Maui;

/// <summary>Registers the MAUI shell services on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class MauiHostExtensions
{
    /// <summary>
    /// Make this a MAUI-hosted application: registers the shell contracts this platform can honour
    /// (<see cref="IClipboardService"/>, <see cref="IUrlLauncher"/>, <see cref="IUiInteraction"/>,
    /// <see cref="IFileDialogs"/>, <see cref="IUiDispatcher"/>), each with <c>TryAdd</c> so an app
    /// registration wins.
    /// <para>
    /// <b>It registers NO <see cref="IShenoraRunner"/>, deliberately.</b> MAUI owns the loop, so
    /// <see cref="ShenoraApplication.Run"/> — contractually "blocks until shutdown" — has no honest
    /// implementation here. Drive <see cref="ShenoraApplication.Start"/> and
    /// <see cref="ShenoraApplication.Stop"/> from the app's own lifecycle instead; both are
    /// idempotent precisely because Android recreates an activity on a configuration change.
    /// </para>
    /// <para>
    /// The services that are NOT here are the point of the capability rule: no drop zones, no tray,
    /// no secondary windows, no window state. Those are absent on this platform rather than
    /// implemented differently, and portable logic asking for one gets a named refusal
    /// (<see cref="ShellCapability"/>) rather than a null or a silent nothing.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="dispatcher">
    /// The MAUI dispatcher UI work marshals to — typically <c>Application.Current.Dispatcher</c>, or
    /// the hosting page's. Required rather than resolved, because Core has no way to find it and a
    /// silently-missing UI dispatcher swallows UI work.
    /// </param>
    /// <param name="onError">Reports a failure from posted UI work or a backgrounded URL open.</param>
    public static ShenoraApplicationBuilder UseMaui(this ShenoraApplicationBuilder builder,
        IDispatcher dispatcher, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dispatcher);

        builder.Services.TryAddSingleton<IUiDispatcher>(_ => new MauiUiDispatcher(dispatcher, onError));
        builder.Services.TryAddSingleton<IClipboardService, MauiClipboardService>();
        builder.Services.TryAddSingleton<IUrlLauncher>(_ => new MauiUrlLauncher(onError));
        builder.Services.TryAddSingleton<IUiInteraction, MauiUiInteraction>();
        builder.Services.TryAddSingleton<IFileDialogs, MauiFileDialogs>();

        return builder;
    }
}
