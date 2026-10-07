using Shenora.Core.Shell;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// Holds a <see cref="SingleInstanceGuard"/> on a dedicated thread — the guard's contract is
/// cross-process, and an OS mutex is per-thread reentrant, so in-process "another instance"
/// simulations must own from a different thread (as a second process would). Dispose releases on
/// the owning thread (the clean-shutdown handoff); <c>abandon: true</c> lets the thread exit
/// still holding (the crashed-predecessor path). <c>activated</c> makes it the running instance that listens.
/// </summary>
internal sealed class ThreadHeldGuard : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _release = new();
    private bool _disposed;
    private SingleInstanceGuard? _guard;

    public bool Acquired { get; private set; }

    public ThreadHeldGuard(string applicationName, string scope, bool abandon = false, Action<SingleInstanceLaunch>? activated = null)
    {
        using var acquired = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            var guard = _guard = new SingleInstanceGuard(applicationName, scope);
            Acquired = guard.TryAcquire() is SingleInstanceResult.Acquired;
            if (Acquired && activated is not null) guard.Listen(activated);
            acquired.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            if (!abandon) guard.Dispose(); // release on the OWNING thread; else exit holding
        })
        { IsBackground = true };
        _thread.Start();
        acquired.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>The running instance begins its shutdown: later launches stop reaching it; the scope stays held.</summary>
    public void StopListening() => _guard!.StopListening();

    /// <summary>The running instance, started, begins taking later launches.</summary>
    public void Listen(Action<SingleInstanceLaunch> activated) => _guard!.Listen(activated);

    /// <summary>Let the holding thread finish (releasing or abandoning) and join it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(30));
        _release.Dispose();
    }
}
