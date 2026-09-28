using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's UI dispatcher against a fake UI thread (a queue drained on demand), so its state
/// machine and the IUiDispatcher contract are checked without CEF.
/// </summary>
public class CefUiDispatcherTests
{
    private sealed class FakeUi
    {
        public readonly Queue<Action> Posted = new();
        public bool OnUi;
        public bool Accepting = true;

        public bool Post(Action work)
        {
            if (!Accepting) return false;
            Posted.Enqueue(work);
            return true;
        }

        /// <summary>Run everything posted, as the UI thread would.</summary>
        public void Drain()
        {
            var was = OnUi;
            OnUi = true;
            while (Posted.TryDequeue(out var work)) work();
            OnUi = was;
        }
    }

    private static (CefUiDispatcher Dispatcher, FakeUi Ui) Make()
    {
        var ui = new FakeUi();
        return (new CefUiDispatcher(ui.Post, () => ui.OnUi), ui);
    }

    [Fact]
    public void Nothing_runs_before_CEF_is_ready_and_the_caller_is_told()
    {
        var (dispatcher, ui) = Make();

        Assert.Equal(UiTargetState.NotReady, dispatcher.State);
        Assert.False(dispatcher.Post(() => { }));
        Assert.Empty(ui.Posted);
    }

    [Fact]
    public void On_the_UI_thread_work_runs_inline_and_off_it_work_is_posted()
    {
        var (dispatcher, ui) = Make();
        dispatcher.MarkReady();
        var ran = 0;

        ui.OnUi = true;
        Assert.True(dispatcher.Post(() => ran++));
        Assert.Equal(1, ran);

        ui.OnUi = false;
        Assert.True(dispatcher.Post(() => ran++));
        Assert.Equal(1, ran);
        ui.Drain();
        Assert.Equal(2, ran);
    }

    [Fact]
    public void A_throwing_body_never_escapes_Post()
    {
        var (dispatcher, ui) = Make();
        dispatcher.MarkReady();
        ui.OnUi = true;

        Assert.True(dispatcher.Post(() => throw new InvalidOperationException("app bug")));
    }

    [Fact]
    public void A_refused_post_means_the_UI_thread_is_gone()
    {
        var (dispatcher, ui) = Make();
        dispatcher.MarkReady();
        ui.Accepting = false;

        Assert.False(dispatcher.Post(() => { }));
        Assert.Equal(UiTargetState.Gone, dispatcher.State);
    }

    [Fact]
    public async Task InvokeAsync_faults_by_state_rather_than_hanging()
    {
        var (dispatcher, _) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => { }));

        dispatcher.MarkReady();
        dispatcher.MarkGone();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => { }));
    }

    [Fact]
    public async Task InvokeAsync_returns_the_bodys_result_and_its_fault()
    {
        var (dispatcher, ui) = Make();
        dispatcher.MarkReady();

        var answer = dispatcher.InvokeAsync(() => Task.FromResult(42));
        var failure = dispatcher.InvokeAsync(() => Task.FromException<int>(new InvalidOperationException("boom")));
        ui.Drain();

        Assert.Equal(42, await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvokeAsync_observes_its_token_when_the_UI_thread_never_runs_it()
    {
        var (dispatcher, _) = Make();
        dispatcher.MarkReady();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Posted, never drained: a wedged UI thread. The caller must still get an answer.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.InvokeAsync(() => { }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvokeOrDefaultAsync_never_faults()
    {
        var (dispatcher, _) = Make();

        Assert.Equal(-1, await dispatcher.InvokeOrDefaultAsync(() => Task.FromResult(1), -1));
    }

    [Fact]
    public void The_sync_context_resumes_continuations_on_the_UI_thread()
    {
        var (dispatcher, ui) = Make();
        dispatcher.MarkReady();
        var context = new CefUiContext(dispatcher);
        var ranOnUi = false;

        context.Post(_ => ranOnUi = ui.OnUi, null);
        Assert.False(ranOnUi);
        ui.Drain();

        Assert.True(ranOnUi);
    }
}
