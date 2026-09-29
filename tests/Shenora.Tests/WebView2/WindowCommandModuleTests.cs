using Shenora.Windows;
using Shenora.Tests.TestSupport;
using Shenora.Core.Ipc;

namespace Shenora.Tests.WebView2;

/// <summary>
/// Route tests over a real (invisible, handle-created) form. Routes that mutate the form post
/// via BeginInvoke — <c>Application.DoEvents()</c> pumps the queued posts on the test thread.
/// START_DRAG/START_RESIZE are asserted at the response level only (their posted OS
/// move/size-loop handoff needs a live interactive window — the sample e2e's subject).
/// </summary>
public class WindowCommandModuleTests
{
    private static IpcRequest Request(string type, object? payload = null) =>
        IpcRequests.Create(WindowCommandModule.Module, type, payload: payload);

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
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

        var response = await facade.HandleMessageAsync(Request("MINIMIZE"));
        Application.DoEvents();

        Assert.True(response.Success);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
    }

    [Fact]
    public async Task Toggle_maximize_defaults_to_window_state()
    {
        using var form = CreateForm();
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

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
        var facade = new WindowCommandModule(new WindowCommandOptions
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
        var byState = new WindowCommandModule(new WindowCommandOptions { Window = form });
        var bySeam = new WindowCommandModule(new WindowCommandOptions
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
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

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
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

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
        var without = new WindowCommandModule(new WindowCommandOptions { Window = form });
        var with = new WindowCommandModule(new WindowCommandOptions
        {
            Window = form,
            ApplyTheme = applied.Add,
        });

        var refused = await without.HandleMessageAsync(Request("SET_THEME", new { dark = false }));
        Assert.Equal(IpcErrorCodes.NoRoute, refused.Error!.Code);

        var accepted = await with.HandleMessageAsync(Request("SET_THEME", new { dark = false }));
        Application.DoEvents();
        Assert.True(accepted.Success);
        Assert.Equal([false], applied);
    }

    // ── SET_CAPTION_BUTTONS (P5.6) ────────────────────────────────────────────────────────────────
    // The page re-sends this on every layout change, so the parser has to be TOTAL: one odd entry
    // must not cost the other buttons their hit-test, since the result is a caption button that
    // silently stops responding.

    [Fact]
    public async Task Caption_buttons_are_refused_until_the_seam_is_wired()
    {
        using var form = CreateForm();
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

        var response = await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS",
            new { buttons = new[] { new { kind = "maximize", x = 10, y = 0, width = 30, height = 30 } } }));

        // Same shape as SET_THEME: an optional route is NO_HANDLER until its callback exists.
        Assert.Equal(IpcErrorCodes.NoRoute, response.Error!.Code);
    }

    [Fact]
    public async Task Caption_buttons_reach_the_seam_with_every_kind_mapped()
    {
        using var form = CreateForm();
        IReadOnlyList<CaptionButtonRegion>? received = null;
        var facade = new WindowCommandModule(new WindowCommandOptions
        {
            Window = form,
            CoordinateSpace = form,
            SetCaptionButtons = r => received = r,
        });

        await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS", new
        {
            buttons = new[]
            {
                new { kind = "minimize", x = 700, y = 0, width = 30, height = 30 },
                new { kind = "maximize", x = 730, y = 0, width = 30, height = 30 },
                new { kind = "close", x = 760, y = 0, width = 30, height = 30 },
            },
        }));
        Application.DoEvents(); // the seam is invoked through a posted body

        Assert.NotNull(received);
        Assert.Equal(
            [CaptionButtonKind.Minimize, CaptionButtonKind.Maximize, CaptionButtonKind.Close],
            received!.Select(r => r.Kind));
        Assert.Equal(30, received[0].Bounds.Width);
    }

    [Theory]
    [InlineData("MAXIMIZE")]   // case-insensitive: the wire is app-authored text
    [InlineData("Maximize")]
    public async Task Caption_button_kinds_are_case_insensitive(string kind)
    {
        using var form = CreateForm();
        IReadOnlyList<CaptionButtonRegion>? received = null;
        var facade = new WindowCommandModule(new WindowCommandOptions
        {
            Window = form,
            CoordinateSpace = form,
            SetCaptionButtons = r => received = r,
        });

        await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS",
            new { buttons = new[] { new { kind, x = 1, y = 2, width = 30, height = 30 } } }));
        Application.DoEvents();

        Assert.Equal(CaptionButtonKind.Maximize, Assert.Single(received!).Kind);
    }

    [Fact]
    public async Task A_bad_entry_is_skipped_without_costing_the_others_their_hit_test()
    {
        using var form = CreateForm();
        IReadOnlyList<CaptionButtonRegion>? received = null;
        var facade = new WindowCommandModule(new WindowCommandOptions
        {
            Window = form,
            CoordinateSpace = form,
            SetCaptionButtons = r => received = r,
        });

        await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS", new
        {
            buttons = new object[]
            {
                new { kind = "telepathy", x = 0, y = 0, width = 30, height = 30 },  // unknown kind
                new { kind = "maximize", x = 730, y = 0, width = 0, height = 30 },   // zero size
                new { kind = "close", x = 760, y = 0, width = 30, height = 30 },     // fine
            },
        }));
        Application.DoEvents();

        // Rejecting the whole batch over one odd entry would drop the good button's hit-test as
        // collateral — and a caption button that stops responding is invisible until someone clicks.
        Assert.Equal(CaptionButtonKind.Close, Assert.Single(received!).Kind);
    }

    [Fact]
    public async Task An_empty_or_missing_button_list_clears_rather_than_failing()
    {
        using var form = CreateForm();
        IReadOnlyList<CaptionButtonRegion>? received = null;
        var facade = new WindowCommandModule(new WindowCommandOptions
        {
            Window = form,
            CoordinateSpace = form,
            SetCaptionButtons = r => received = r,
        });

        var response = await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS", new { buttons = Array.Empty<object>() }));
        Application.DoEvents();

        // Clearing is a legitimate instruction — a page that hides its title bar must be able to
        // hand every pixel back.
        Assert.True(response.Success);
        Assert.Empty(received!);
    }

    [Fact]
    public async Task Unknown_types_answer_structured_no_route()
    {
        using var form = CreateForm();
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });

        var response = await facade.HandleMessageAsync(Request("NOPE"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoRoute, response.Error!.Code);
        Assert.Equal(WindowCommandModule.Module, response.Error.Parameters!["module"]);
    }

    // The module is mapped once, for the main window, so a page in any other (a SecondaryWindows window's) must
    // command its own: before, its close button closed the app's main window.
    [Fact]
    public async Task A_page_in_another_window_commands_its_own_window()
    {
        using var main = CreateForm();
        using var other = CreateForm();
        var page = new Control();
        other.Controls.Add(page);
        bool mainClosed = false, otherClosed = false;
        main.FormClosed += (_, _) => mainClosed = true;
        other.FormClosed += (_, _) => otherClosed = true;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main });

        using (PageSender.Enter(page)) await facade.HandleMessageAsync(Request("MINIMIZE"));
        Application.DoEvents();
        Assert.Equal(FormWindowState.Minimized, other.WindowState);
        Assert.Equal(FormWindowState.Normal, main.WindowState);

        using (PageSender.Enter(page)) await facade.HandleMessageAsync(Request("CLOSE"));
        Application.DoEvents();
        Assert.True(otherClosed);
        Assert.False(mainClosed);
    }

    // A request still in flight as its window closes: its page is out of the window by then, and it must not fall
    // back to the main one.
    [Fact]
    public async Task A_page_whose_window_is_gone_commands_nothing()
    {
        using var main = CreateForm();
        var mainClosed = false;
        main.FormClosed += (_, _) => mainClosed = true;
        var page = new Control();
        page.Dispose();
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, IsMaximized = () => true });

        IpcResponse close, maximized;
        using (PageSender.Enter(page))
        {
            close = await facade.HandleMessageAsync(Request("CLOSE"));
            maximized = await facade.HandleMessageAsync(Request("IS_MAXIMIZED"));
        }
        Application.DoEvents();

        Assert.True(close.Success);
        Assert.False(mainClosed);
        Assert.False(IpcJson.SerializeToElement(maximized.Data!).GetProperty("maximized").GetBoolean());
    }

    [Fact]
    public async Task A_page_whose_window_is_gone_still_has_no_route_for_what_it_never_had()
    {
        using var main = CreateForm();
        var page = new Control();
        page.Dispose();
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, ApplyTheme = _ => { } });

        IpcResponse theme, unknown;
        using (PageSender.Enter(page))
        {
            theme = await facade.HandleMessageAsync(Request("SET_THEME", new { dark = true }));
            unknown = await facade.HandleMessageAsync(Request("NOPE"));
        }

        Assert.Equal(IpcErrorCodes.NoRoute, theme.Error!.Code);
        Assert.Equal(IpcErrorCodes.NoRoute, unknown.Error!.Code);
    }

    // Held weakly, so work a page started does not keep it alive; once collected, that page is gone, not "no page".
    [Fact]
    public async Task A_page_whose_control_was_collected_commands_nothing()
    {
        using var main = CreateForm();
        var mainClosed = false;
        main.FormClosed += (_, _) => mainClosed = true;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main });

        var scope = EnterAsACollectablePage();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        IpcResponse close;
        using (scope) close = await facade.HandleMessageAsync(Request("CLOSE"));
        Application.DoEvents();

        Assert.True(close.Success);
        Assert.False(mainClosed);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static PageSender.Scope EnterAsACollectablePage() => PageSender.Enter(new Control());

    // An MDI child or a TopLevel = false panel is inside the main window: its page commands the main window, with the
    // options' callbacks, not the embedded form.
    [Fact]
    public async Task A_page_in_a_form_embedded_in_the_main_one_commands_the_main_window()
    {
        using var main = CreateForm();
        var embedded = new Form { TopLevel = false };
        main.Controls.Add(embedded);
        var page = new Control();
        embedded.Controls.Add(page);
        var toggled = 0;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, ToggleMaximize = () => toggled++ });

        using (PageSender.Enter(page)) await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();

        Assert.Equal(1, toggled);
        Assert.Equal(FormWindowState.Normal, embedded.WindowState);
    }

    [Fact]
    public async Task A_page_in_an_MDI_child_commands_the_MDI_parent()
    {
        using var main = CreateForm();
        main.IsMdiContainer = true;
        var child = new Form { MdiParent = main };
        var page = new Control();
        child.Controls.Add(page);
        var toggled = 0;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, ToggleMaximize = () => toggled++ });

        using (PageSender.Enter(page)) await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();

        Assert.Equal(1, toggled);
    }

    private sealed class AppMaximizedForm : Form, IAppMaximizable
    {
        public WindowPlacement AppPlacement => WindowPlacement.Maximized;
        public Rectangle AppRestoreBounds => Bounds;
    }

    [Fact]
    public async Task Another_window_that_maximizes_itself_is_read_by_its_placement()
    {
        using var main = CreateForm();
        using var other = new AppMaximizedForm();
        _ = other.Handle;
        var page = new Control();
        other.Controls.Add(page);
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main });

        IpcResponse maximized;
        using (PageSender.Enter(page)) maximized = await facade.HandleMessageAsync(Request("IS_MAXIMIZED"));

        Assert.True(IpcJson.SerializeToElement(maximized.Data!).GetProperty("maximized").GetBoolean());
    }

    // Two pages in the main window: each one's rectangles are its own, whatever the options' coordinate space.
    [Fact]
    public async Task A_page_in_the_main_window_is_read_against_its_own_control()
    {
        using var main = CreateForm();
        var first = new Control { Bounds = new Rectangle(0, 0, 100, 100) };
        var second = new Control { Bounds = new Rectangle(120, 60, 200, 200) };
        main.Controls.Add(first);
        main.Controls.Add(second);
        _ = first.Handle;
        _ = second.Handle;
        IReadOnlyList<CaptionButtonRegion>? received = null;
        var facade = new WindowCommandModule(new WindowCommandOptions
        {
            Window = main,
            CoordinateSpace = first,
            SetCaptionButtons = regions => received = regions,
        });

        using (PageSender.Enter(second))
            await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS",
                new { buttons = new[] { new { kind = "close", x = 10, y = 0, width = 30, height = 30 } } }));
        Application.DoEvents();

        var scale = DpiHelper.ScaleFromDeviceDpi(second.DeviceDpi);
        var expected = main.PointToClient(second.PointToScreen(new Point((int)Math.Round(10 * scale), 0)));
        Assert.Equal(expected, Assert.Single(received!).Bounds.Location);
    }

    [Fact]
    public async Task A_page_in_the_options_window_gets_the_options_callbacks()
    {
        using var main = CreateForm();
        var page = new Control();
        main.Controls.Add(page);
        var toggled = 0;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, ToggleMaximize = () => toggled++ });

        using (PageSender.Enter(page)) await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
        Application.DoEvents();

        Assert.Equal(1, toggled);
    }

    [Fact]
    public async Task Another_windows_theme_has_no_route_and_never_reaches_the_options_callback()
    {
        using var main = CreateForm();
        using var other = CreateForm();
        var page = new Control();
        other.Controls.Add(page);
        bool? applied = null;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main, ApplyTheme = dark => applied = dark });

        IpcResponse response;
        using (PageSender.Enter(page)) response = await facade.HandleMessageAsync(Request("SET_THEME", new { dark = false }));
        Application.DoEvents();

        Assert.Equal(IpcErrorCodes.NoRoute, response.Error!.Code);
        Assert.Null(applied);
    }

    [Fact]
    public async Task An_optimized_form_elsewhere_maximizes_and_takes_caption_buttons_its_own_way()
    {
        using var main = CreateForm();
        using var other = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, 800, 600),
            ShowInTaskbar = false,
        };
        // The page does not fill its window, so its CSS px must be read against the page, not the form.
        var page = new Control { Bounds = new Rectangle(100, 50, 600, 400) };
        other.Controls.Add(page);
        _ = other.Handle;
        _ = page.Handle;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = main });

        IpcResponse captions;
        using (PageSender.Enter(page))
            captions = await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTONS",
                new { buttons = new[] { new { kind = "close", x = 10, y = 0, width = 30, height = 30 } } }));
        Application.DoEvents();

        Assert.True(captions.Success);
        var scale = DpiHelper.ScaleFromDeviceDpi(page.DeviceDpi);
        var centre = other.PointToScreen(new Point(100 + (int)(25 * scale), 50 + (int)(15 * scale)));
        Assert.Equal(20 /* HTCLOSE */, (int)SendMessage(other.Handle, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero,
            (IntPtr)(((centre.Y & 0xFFFF) << 16) | (centre.X & 0xFFFF))));

        IpcResponse maximized;
        using (PageSender.Enter(page))
        {
            await facade.HandleMessageAsync(Request("TOGGLE_MAXIMIZE"));
            Application.DoEvents();
            maximized = await facade.HandleMessageAsync(Request("IS_MAXIMIZED"));
        }

        // Its manual work-area maximize, which WindowState never shows.
        Assert.Equal(WindowPlacement.Maximized, other.AppPlacement);
        Assert.Equal(FormWindowState.Normal, other.WindowState);
        Assert.True(IpcJson.SerializeToElement(maximized.Data!).GetProperty("maximized").GetBoolean());
        Assert.Equal(FormWindowState.Normal, main.WindowState);
        other.SetCaptionButtons(null);
    }

    // The page colours the buttons the window paints; a window that does not paint them has no route for it.
    [Fact]
    public async Task Caption_button_colours_reach_a_window_that_paints_its_buttons()
    {
        using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true, NativeCaptionButtons = true })
        {
            ShowInTaskbar = false,
        };
        _ = form.Handle;
        var facade = new WindowCommandModule(new WindowCommandOptions { Window = form });
        var colors = new
        {
            surface = "#305080", hover = "#ffffff22", pressed = "#fff1", glyph = "#fff",
            closeHover = "#c42b1c", closePressed = "#c42b1ce6", inactiveGlyph = "#ffffff5a",
        };

        Assert.True((await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTON_COLORS", new { colors }))).Success);
        Application.DoEvents();
        var set = Assert.IsType<CaptionButtonColors>(form.CaptionButtonColors);
        Assert.Equal(Color.FromArgb(255, 0x30, 0x50, 0x80), set.Surface);
        Assert.Equal(Color.FromArgb(0x11, 255, 255, 255), set.Pressed);
        Assert.Equal(Color.FromArgb(0x5A, 255, 255, 255), set.InactiveGlyph);
        Assert.Null(set.CloseGlyphHot);

        var bad = await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTON_COLORS", new { colors = colors with { glyph = "white" } }));
        Assert.Equal(IpcErrorCodes.InvalidPayloadValue, bad.Error!.Code);

        Assert.True((await facade.HandleMessageAsync(Request("SET_CAPTION_BUTTON_COLORS"))).Success);
        Application.DoEvents();
        Assert.Null(form.CaptionButtonColors);   // back to the fallback

        using var plain = CreateForm();
        var none = await new WindowCommandModule(new WindowCommandOptions { Window = plain })
            .HandleMessageAsync(Request("SET_CAPTION_BUTTON_COLORS", new { colors }));
        Assert.Equal(IpcErrorCodes.NoRoute, none.Error!.Code);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
