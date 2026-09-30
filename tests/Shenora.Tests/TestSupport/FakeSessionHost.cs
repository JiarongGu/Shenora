using System.Collections.Concurrent;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// A UI thread of the test's own: one thread running a message loop, so work posted to it runs in order, off the
/// caller's stack, and on one thread, as a shell's would. Dispose ends the loop.
/// </summary>
internal sealed class TestUiThread : UiDispatcherBase, IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var work in _work.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    public override UiTargetState State => _work.IsAddingCompleted ? UiTargetState.Gone : UiTargetState.Ready;

    public override bool IsOnUiThread => Thread.CurrentThread == _thread;

    protected override bool TryPost(Action work, out Exception? failure)
    {
        failure = null;
        try { _work.Add(work); return true; }
        catch (InvalidOperationException ex) { failure = ex; return false; }
    }

    public void Dispose() => _work.CompleteAdding();

    // Continuations of work that ran here come back here, as they would on a shell's UI thread.
    private sealed class Context(TestUiThread ui) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ui.TryPost(() => d(state), out _);
    }
}

/// <summary>A session host with no engine (D91): its browsers are <see cref="FakeSessionBrowser"/>s and its window a
/// <see cref="FakeSessionWindow"/> the test closes as a person or the app would.</summary>
internal sealed class FakeSessionHost(TestUiThread ui) : ISessionHost
{
    public IUiDispatcher Ui => ui;

    public FakeSessionWindow? LastWindow { get; private set; }

    public ISessionBrowserContext CreateContext() => new NoContext();

    public Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken) =>
        Task.FromResult<ISessionBrowser>(new FakeSessionBrowser());

    public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken)
    {
        LastWindow = new FakeSessionWindow(ui, definition.Revealed);
        return Task.FromResult<ISessionWindow>(LastWindow);
    }

    private sealed class NoContext : ISessionBrowserContext
    {
        public void Dispose() { }
    }
}

/// <summary>An interactive session's window with no screen behind it.</summary>
internal sealed class FakeSessionWindow(TestUiThread ui, bool revealed) : ISessionWindow
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ISessionBrowser Browser { get; } = new FakeSessionBrowser();

    public Func<bool, bool>? Closing { get; set; }

    public bool IsRevealed { get; private set; } = revealed;

    public Task Closed => _closed.Task;

    public void Reveal() => IsRevealed = true;

    public void FitToContent(int cssWidth, int cssHeight) { }

    public void Close() => _closed.TrySetResult();

    /// <summary>Someone other than the session closes it, on the UI thread: a person (<paramref name="byUser"/>), or the
    /// app or the OS. True when it closed; false when the session held it.</summary>
    public Task<bool> CloseByAsync(bool byUser) => ui.InvokeAsync(() =>
    {
        if (Closing is { } closing && !closing(byUser)) return Task.FromResult(false);
        _closed.TrySetResult();
        return Task.FromResult(true);
    });
}
