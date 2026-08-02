using Shenora.Core;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The ONE UI-thread marshalling seam (D20). These tests pin the semantics that a single
/// availability bool could NOT express — the reason the contract is three-state at all: three call
/// sites in the kit have different, review-earned pre-handle policies, and collapsing "no handle yet"
/// into "gone" would silently re-break two previously-fixed defects.
/// </summary>
public class WinFormsUiDispatcherTests
{
    /// <summary>A form with a REALIZED handle (creating it is what makes the target Ready).</summary>
    private static Form Realized()
    {
        var form = new Form();
        _ = form.Handle; // force handle creation on this thread
        return form;
    }

    [Fact]
    public void State_distinguishes_not_ready_from_ready_from_gone()
    {
        Sta.Run(() =>
        {
            var form = new Form();
            var dispatcher = new WinFormsUiDispatcher(form);

            // Not shown, no handle: there is nothing to marshal TO — but the target is not dead.
            Assert.Equal(UiTargetState.NotReady, dispatcher.State);

            _ = form.Handle;
            Assert.Equal(UiTargetState.Ready, dispatcher.State);

            form.Dispose();
            Assert.Equal(UiTargetState.Gone, dispatcher.State);
        });
    }

    [Fact]
    public void Post_runs_inline_when_already_on_the_ui_thread()
    {
        Sta.Run(() =>
        {
            using var form = Realized();
            var dispatcher = new WinFormsUiDispatcher(form);
            var ran = false;

            Assert.True(dispatcher.IsOnUiThread);
            Assert.True(dispatcher.Post(() => ran = true));
            Assert.True(ran); // inline: no pump needed, which is what START_DRAG-style timing relies on
        });
    }

    [Fact]
    public void Post_returns_false_when_not_ready_and_when_gone_and_never_runs_the_work()
    {
        Sta.Run(() =>
        {
            var form = new Form();
            var dispatcher = new WinFormsUiDispatcher(form);
            var ran = false;

            Assert.False(dispatcher.Post(() => ran = true));   // NotReady
            Assert.Equal(UiTargetState.NotReady, dispatcher.State);

            form.Dispose();
            Assert.False(dispatcher.Post(() => ran = true));   // Gone
            Assert.Equal(UiTargetState.Gone, dispatcher.State);

            // False means "not Ready" — the CALLER decides what to do, and the work never ran here.
            Assert.False(ran);
        });
    }

    [Fact]
    public void A_throwing_post_body_is_reported_not_thrown_on_the_inline_path()
    {
        Sta.Run(() =>
        {
            using var form = Realized();
            Exception? reported = null;
            var dispatcher = new WinFormsUiDispatcher(form, ex => reported = ex);

            // Must NOT throw to the caller just because the caller happened to be on the UI thread —
            // otherwise Post grows a throw path it never had off-thread.
            Assert.True(dispatcher.Post(() => throw new InvalidOperationException("boom")));
            Assert.IsType<InvalidOperationException>(reported);
        });
    }

    [Fact]
    public void A_throwing_post_body_with_no_reporter_is_swallowed()
    {
        Sta.Run(() =>
        {
            using var form = Realized();
            var dispatcher = new WinFormsUiDispatcher(form);
            Assert.True(dispatcher.Post(() => throw new InvalidOperationException("boom")));
        });
    }

    [Fact]
    public void A_faulting_async_post_body_is_reported_and_never_becomes_an_unobserved_crash()
    {
        Sta.Run(() =>
        {
            using var form = Realized();
            Exception? reported = null;
            var dispatcher = new WinFormsUiDispatcher(form, ex => reported = ex);

            // The whole reason Post(Func<Task>) exists: a hand-rolled BeginInvoke(async …) drops the
            // task and its fault is unobservable.
            Assert.True(dispatcher.Post(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("async boom");
            }));

            // Let the continuation run on this thread's pump.
            for (var i = 0; i < 20 && reported is null; i++)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Assert.IsType<InvalidOperationException>(reported);
        });
    }

    [Fact]
    public async Task InvokeAsync_faults_with_the_state_meaning_rather_than_hanging()
    {
        Form? notReady = null;
        Form? gone = null;
        Sta.Run(() =>
        {
            notReady = new Form();
            gone = Realized();
            gone.Dispose();
        });

        // NotReady is retryable; Gone never will be. A caller must be able to tell them apart, and
        // NEITHER may return a task that simply never completes.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WinFormsUiDispatcher(notReady!).InvokeAsync(() => { }));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => new WinFormsUiDispatcher(gone!).InvokeAsync(() => { }));

        notReady!.Dispose();
    }

    [Fact]
    public void InvokeAsync_returns_the_bodys_value_on_the_ui_thread()
    {
        Sta.Run(() =>
        {
            using var form = Realized();
            var dispatcher = new WinFormsUiDispatcher(form);

            var task = dispatcher.InvokeAsync(() => Task.FromResult(42));
            Assert.True(task.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(42, task.Result);
        });
    }

    [Fact]
    public async Task InvokeAsync_observes_its_token_even_when_the_ui_thread_never_runs_the_body()
    {
        // THE invariant this seam exists for. An operation that accepts a CancellationToken and then
        // ignores it cannot be cancelled when the UI thread is wedged — which is how one blocked page
        // permanently starved a session pool (a leased permit that never comes back). Here the form's
        // thread deliberately never pumps, so the posted body CANNOT run: the only way the caller
        // escapes is if the token is honored.
        Form? form = null;
        var created = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            form = new Form();
            _ = form.Handle;
            created.Set();
            release.Wait(TimeSpan.FromSeconds(20)); // holds the thread WITHOUT pumping messages
            form.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(created.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            var dispatcher = new WinFormsUiDispatcher(form!);
            Assert.Equal(UiTargetState.Ready, dispatcher.State);

            using var cts = new CancellationTokenSource();
            var pending = dispatcher.InvokeAsync(() => Task.FromResult(1), cts.Token);
            Assert.False(pending.IsCompleted); // nothing is pumping, so the body has not run

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task InvokeOrDefaultAsync_never_faults()
    {
        Task<int> thrown = null!;
        Task<int> notReady = null!;

        // Every Form is created, used and disposed on the STA thread (the repo's earned rule), and
        // both calls complete SYNCHRONOUSLY there — one runs inline because we are on the UI thread,
        // the other faults immediately because the target has no handle. So the awaits below need no
        // message pump, and there is no blocking wait anywhere.
        Sta.Run(() =>
        {
            using var form = Realized();
            var dispatcher = new WinFormsUiDispatcher(form);

            // The co-browse input contract: one bad message must not fault the whole session.
            thrown = dispatcher.InvokeOrDefaultAsync<int>(
                () => throw new InvalidOperationException("boom"), fallback: -1);

            using var never = new Form();
            notReady = new WinFormsUiDispatcher(never)
                .InvokeOrDefaultAsync(() => Task.FromResult(1), fallback: -1);
        });

        Assert.Equal(-1, await thrown);
        Assert.Equal(-1, await notReady);
    }

    [Fact]
    public async Task MainFormUiDispatcher_resolves_the_form_lazily_and_reports_a_disposed_one_as_gone()
    {
        // The container is built BEFORE the runner creates the main form, and the runner never clears
        // the registration afterwards — so both "not registered yet" and "registered but disposed"
        // are real runtime states, not theory.
        var interaction = new FakeInteraction();
        var dispatcher = new MainFormUiDispatcher(interaction);

        Assert.Equal(UiTargetState.NotReady, dispatcher.State);
        Assert.False(dispatcher.Post(() => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => { }));

        Form? form = null;
        Sta.Run(() =>
        {
            form = Realized();
            interaction.MainForm = form;
            Assert.Equal(UiTargetState.Ready, dispatcher.State);
            form.Dispose();
        });

        Assert.Equal(UiTargetState.Gone, dispatcher.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => { }));
    }

    private sealed class FakeInteraction : IFormInteraction
    {
        public Form? MainForm;

        public void SetMainForm(Form form) => MainForm = form;
        public Form? GetMainForm() => MainForm;
        public IntPtr GetMainFormHandle() => MainForm?.IsHandleCreated == true ? MainForm.Handle : IntPtr.Zero;
        public void BlockInteraction() { }
        public void UnblockInteraction() { }
    }
}
