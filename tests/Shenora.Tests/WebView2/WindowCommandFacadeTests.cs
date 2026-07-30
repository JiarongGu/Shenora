using Shenora.Ipc;
using Shenora.WebView2;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WebView2;

/// <summary>
/// Route tests over a real (invisible, handle-created) form. Routes that mutate the form post
/// via BeginInvoke — <c>Application.DoEvents()</c> pumps the queued posts on the test thread.
/// START_DRAG/START_RESIZE are asserted at the response level only (their posted OS
/// move/size-loop handoff needs a live interactive window — the sample e2e's subject).
/// </summary>
public class WindowCommandFacadeTests
{
    private static IpcRequest Request(string type, object? payload = null) =>
        IpcRequests.Create(WindowCommandFacade.Module, type, payload: payload);

    private static Form CreateForm()
    {
        var form = new Form();
        _ = form.Handle; // BeginInvoke and Close→FormClosed need a created handle
        return form;
    }

    [Fact]
    public async Task Minimize_posts_to_the_form()
    {
        using var form = CreateForm();
        var facade = new WindowCommandFacade(new WindowCommandOptions { Window = form });

        var response = await facade.HandleMessageAsync(Request("MINIMIZE"));
        Application.DoEvents();

        Assert.True(response.Success);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
    }

    [Fact]
    public async Task Toggle_maximize_defaults_to_window_state()
    {
        using var form = CreateForm();
        var facade = new WindowCommandFacade(new WindowCommandOptions { Window = form });

        await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();
        Assert.Equal(FormWindowState.Maximized, form.WindowState);

        await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();
        Assert.Equal(FormWindowState.Normal, form.WindowState);
    }

    [Fact]
    public async Task Toggle_maximize_uses_the_seam_when_provided()
    {
        using var form = CreateForm();
        var toggled = 0;
        var facade = new WindowCommandFacade(new WindowCommandOptions
        {
            Window = form,
            ToggleMaximize = () => toggled++,
        });

        await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();

        Assert.Equal(1, toggled);
        Assert.Equal(FormWindowState.Normal, form.WindowState); // the default path was replaced
    }

    [Fact]
    public async Task Is_maximized_reads_the_seam_or_window_state()
    {
        using var form = CreateForm();
        var byState = new WindowCommandFacade(new WindowCommandOptions { Window = form });
        var bySeam = new WindowCommandFacade(new WindowCommandOptions
        {
            Window = form,
            IsMaximized = () => true, // e.g. OptimizedForm.IsAppMaximized — never in WindowState
        });

        var stateResponse = await byState.HandleMessageAsync(Request("IS_MAXIMIZED"));
        var seamResponse = await bySeam.HandleMessageAsync(Request("IS_MAXIMIZED"));

        Assert.False(IpcJson.SerializeToElement(stateResponse.Data!).GetProperty("maximized").GetBoolean());
        Assert.True(IpcJson.SerializeToElement(seamResponse.Data!).GetProperty("maximized").GetBoolean());
    }

    [Fact]
    public async Task Close_posts_to_the_form()
    {
        using var form = CreateForm();
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        var facade = new WindowCommandFacade(new WindowCommandOptions { Window = form });

        await facade.HandleMessageAsync(Request("CLOSE"));
        Application.DoEvents();

        Assert.True(closed);
    }

    [Theory]
    [InlineData("START_DRAG")]
    [InlineData("START_RESIZE")]
    public async Task Drag_and_resize_routes_answer_success(string type)
    {
        using var form = CreateForm();
        var facade = new WindowCommandFacade(new WindowCommandOptions { Window = form });

        // DISPATCHED FROM A WORKER THREAD ON PURPOSE — do not "simplify" this to a direct await.
        // The handoff body calls SendMessage(WM_NCLBUTTONDOWN), which enters the OS modal move/size
        // loop and does not return until the loop ends. H4.2 made the dispatcher run a body INLINE
        // when the caller is already on the UI thread (correct: the loop must start while the mouse
        // button is down), and this form's UI thread is the test thread — so awaiting directly ran
        // the modal loop IN THE TEST. It blocked ~17 s in the suite and hung indefinitely when run
        // alone; collection-level parallelism had been masking it in the wall clock since H4.2, and
        // the old "deliberately NOT pumped" comment here had silently become false.
        // From a worker thread the state check passes, InvokeRequired is true, and the body is
        // BeginInvoke'd to a queue this test never pumps — which is what the comment always claimed.
        var response = await Task.Run(() => facade.HandleMessageAsync(Request(type, new { edge = "topLeft" })));

        Assert.True(response.Success);
        Assert.False(form.IsDisposed); // the handoff was queued, never run — no modal loop entered
    }

    [Fact]
    public async Task Set_theme_requires_the_seam_and_passes_dark()
    {
        using var form = CreateForm();
        var applied = new List<bool>();
        var without = new WindowCommandFacade(new WindowCommandOptions { Window = form });
        var with = new WindowCommandFacade(new WindowCommandOptions
        {
            Window = form,
            ApplyTheme = applied.Add,
        });

        var refused = await without.HandleMessageAsync(Request("SET_THEME", new { dark = false }));
        Assert.Equal(IpcErrorCodes.NoHandler, refused.Error!.Code);

        var accepted = await with.HandleMessageAsync(Request("SET_THEME", new { dark = false }));
        Application.DoEvents();
        Assert.True(accepted.Success);
        Assert.Equal([false], applied);
    }

    [Fact]
    public async Task Unknown_types_answer_structured_no_handler()
    {
        using var form = CreateForm();
        var facade = new WindowCommandFacade(new WindowCommandOptions { Window = form });

        var response = await facade.HandleMessageAsync(Request("NOPE"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
        Assert.Equal(WindowCommandFacade.Module, response.Error.Parameters!["module"]);
    }
}
