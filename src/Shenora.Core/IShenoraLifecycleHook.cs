namespace Shenora.Core;

/// <summary>
/// Startup/shutdown participation for composed packages and apps. Register implementations in DI
/// (or use the <see cref="ShenoraApplicationBuilder.OnStarting"/> /
/// <see cref="ShenoraApplicationBuilder.OnStopping"/> conveniences); the runner invokes them.
///
/// The contract every runner must honor (ordering is the point — it encodes the family's
/// measured startup sequence):
/// <list type="bullet">
/// <item><see cref="OnStarting"/> runs in registration order AFTER the single-instance gate and
/// process init, BEFORE the main window is created — so a hook that takes an OS lock (e.g. the
/// WebView2 environment prewarm, which locks the user-data folder) can never race a second
/// instance, yet still overlaps window creation.</item>
/// <item><see cref="OnStopping"/> runs in REVERSE registration order after the loop ends, before
/// the service provider is disposed. It runs even when startup failed partway, so implementations
/// must tolerate their own start never having happened. Hooks must not throw; a throw during
/// shutdown is swallowed (the family's never-block-close discipline).</item>
/// </list>
/// </summary>
public interface IShenoraLifecycleHook
{
    /// <summary>After the gate and process init, before the main window exists.</summary>
    void OnStarting(ShenoraApplication app) { }

    /// <summary>After the loop ends (reverse registration order), before disposal.</summary>
    void OnStopping(ShenoraApplication app) { }
}

/// <summary>Delegate-backed hook used by the builder's <c>OnStarting</c>/<c>OnStopping</c> conveniences.</summary>
internal sealed class DelegateLifecycleHook(
    Action<ShenoraApplication>? starting, Action<ShenoraApplication>? stopping) : IShenoraLifecycleHook
{
    public void OnStarting(ShenoraApplication app) => starting?.Invoke(app);
    public void OnStopping(ShenoraApplication app) => stopping?.Invoke(app);
}
