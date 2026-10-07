using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Core.Events;

namespace Shenora.Chromium;

/// <summary>
/// What a splash's setup reaches: its state, the app's services, and the moments it may hook. Hooks are registered
/// during setup. Callbacks may arrive on any thread; state is safe to set from any of them.
/// </summary>
public sealed class SplashContext
{
    private readonly ISplashSessionSink _sink;
    private readonly IEventBus? _bus;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly List<Func<CancellationToken, Task>> _shown = [];
    private readonly List<Action> _opened = [];
    private readonly List<Action> _ready = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _started;
    private volatile bool _lifted;

    internal SplashContext(IServiceProvider services, bool? systemDark, IEventBus? bus, ISplashSessionSink sink, ILogger? log)
    {
        Services = services;
        SystemDark = systemDark;
        _bus = bus;
        _sink = sink;
        _log = log;
    }

    /// <summary>The app's services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Whether the operating system's app theme was dark as the splash started; null when it could not tell.
    /// The page learns the user's theme only once it runs, so this is how a splash matches it first.</summary>
    public bool? SystemDark { get; }

    /// <summary>A value the render function reads. Setting a different one draws the splash again.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="initial">The first value.</param>
    public SplashState<T> State<T>(T initial) => new(this, initial);

    /// <summary>
    /// Work the app does while the splash shows: started on the thread pool as the splash first shows, alongside the
    /// other <see cref="OnShown"/> work and Chromium's own start, before <c>OnStarting</c>. The splash stays until every
    /// one has finished; one that throws is logged and no longer waited on. The token is cancelled when the app stops,
    /// never by the splash lifting. ⚠ The UI dispatcher is not ready yet.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <exception cref="InvalidOperationException">Registered after the setup returned.</exception>
    public void OnShown(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Register a splash's boot work during its setup: the work has already started.");
            _shown.Add(work);
        }
    }

    /// <summary>Chromium has started and the main window exists, about to show under the splash. On Chromium's UI
    /// thread, before the window shows, so a slow callback holds the window back.</summary>
    /// <param name="callback">The callback.</param>
    public void OnWindowOpened(Action callback) => Add(_opened, callback);

    /// <summary>The page's ready handshake. On Chromium's UI thread.</summary>
    /// <param name="callback">The callback.</param>
    public void OnPageReady(Action callback) => Add(_ready, callback);

    /// <summary>The app's own events on its <see cref="IEventBus"/>, until the splash lifts. No bus, no events.</summary>
    /// <param name="module">The emitting module.</param>
    /// <param name="type">The event type.</param>
    /// <param name="handler">Called for each event, on the emitter's thread.</param>
    public void Subscribe(string module, string type, Action<EventMessage> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(handler);
        if (_bus is null) return;
        var subscription = _bus.Subscribe(module, type, message =>
        {
            if (!_lifted)
                AppCallback.Run(() => handler(message),
                    ex => AppCallback.Log(_log, () => $"[Shenora.Chromium] A splash handler for {module}/{type} failed", LogLevel.Warning, ex));
            return Task.CompletedTask;
        });
        lock (_gate)
        {
            if (!_lifted)
            {
                _subscriptions.Add(subscription);
                return;
            }
        }
        subscription.Dispose();
    }

    /// <summary>Lift the splash now, whatever it was waiting for.</summary>
    public void Close() => _sink.Close();

    /// <summary>The boot work, taken once by the session as it starts it; from here on a registration is refused. One
    /// lock, so no registration lands between the two and is accepted without ever running.</summary>
    internal IReadOnlyList<Func<CancellationToken, Task>> TakeShownWork()
    {
        lock (_gate)
        {
            _started = true;
            return [.. _shown];
        }
    }

    internal void Invalidate()
    {
        if (!_lifted) _sink.Invalidate();
    }

    internal void RaiseWindowOpened() => Raise(_opened, "window-opened");

    internal void RaisePageReady() => Raise(_ready, "page-ready");

    /// <summary>The splash is gone: the subscriptions go with it, and state no longer draws.</summary>
    internal void Lifted()
    {
        IDisposable[] subscriptions;
        lock (_gate)
        {
            if (_lifted) return;
            _lifted = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
            AppCallback.Run(subscription.Dispose, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Ending a splash subscription failed", LogLevel.Warning, ex));
    }

    private void Add(List<Action> list, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate) list.Add(callback);
    }

    private void Raise(List<Action> list, string moment)
    {
        if (_lifted) return;
        Action[] callbacks;
        lock (_gate) callbacks = [.. list];
        foreach (var callback in callbacks)
            AppCallback.Run(callback, ex => AppCallback.Log(_log, () => $"[Shenora.Chromium] A splash {moment} callback failed", LogLevel.Warning, ex));
    }
}

/// <summary>A value a splash's render function reads (<see cref="SplashContext.State{T}"/>). Safe from any thread.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class SplashState<T>
{
    private readonly SplashContext _owner;
    private readonly Lock _gate = new();
    private T _value;

    internal SplashState(SplashContext owner, T initial)
    {
        _owner = owner;
        _value = initial;
    }

    /// <summary>The value. Setting a different one draws the splash again; after it lifts, nothing is drawn.</summary>
    public T Value
    {
        get
        {
            lock (_gate) return _value;
        }
        set
        {
            lock (_gate)
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
            }
            _owner.Invalidate();
        }
    }
}
