using Shenora.Core.Shell;
using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Chromium;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The Chromium engine in a WinForms app, where it can go wrong without CEF: the test output has no CEF runtime,
/// which is exactly an app that forgot the package whose build lays it out. The working path is proven against a
/// real CEF in a WinForms probe (D83).
/// </summary>
public class ChromiumViewTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.Chromium",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });

    [Fact]
    public void A_view_whose_engine_is_not_running_opens_nothing_and_does_not_throw_from_handle_creation()
    {
        Sta.Run(() =>
        {
            using var view = new ChromiumView(new ChromiumEngine(new ChromiumEngineOptions()));

            view.CreateControl();   // WinForms answers a throw here with a blocking dialog

            Assert.True(view.IsHandleCreated);
            Assert.Null(view.Browser);
        });
    }

    // A view's page has its IPC dispatched in work its browser posts to the view's thread, from CEF's, so that work
    // runs as the view's page and a window command acts on the view's window (WindowCommandModule).
    [Fact]
    public void Work_the_views_browser_posts_to_its_thread_runs_as_the_views_page_awaits_included()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            var view = new ChromiumView(new ChromiumEngine(new ChromiumEngineOptions()));
            form.Controls.Add(view);
            _ = form.Handle;
            _ = view.Handle;
            var ui = view.BrowserOptions().UiDispatcher!;
            Control? during = null, afterAwait = null;
            var done = false;

            var posted = Task.Run(() => ui.Post(async () =>
            {
                during = PageSender.Current;
                await Task.Yield();
                afterAwait = PageSender.Current;
                done = true;
            })).Result;
            for (var i = 0; i < 400 && !done; i++)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }

            Assert.True(posted);
            Assert.Same(view, during);
            Assert.Same(view, afterAwait);
        });
    }

    // The page's drag area is the window's caption. Moving is not tested here: it enters the OS move loop, which the
    // WinForms probe measures instead.
    [Fact]
    public void A_double_click_on_the_drag_area_maximizes_and_restores()
    {
        Sta.Run(() =>
        {
            using var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(0, 0, 800, 600),
                ShowInTaskbar = false,
            };
            var view = new ChromiumView(new ChromiumEngine(new ChromiumEngineOptions())) { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            _ = form.Handle;
            _ = view.Handle;
            var pressed = view.BrowserOptions().DragAreaPressed!;

            pressed(new ChromiumDragAreaPress(ChromiumDragAreaAction.ToggleMaximize, default));
            Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
            pressed(new ChromiumDragAreaPress((ChromiumDragAreaAction)99, default));   // a later version's action
            Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
            pressed(new ChromiumDragAreaPress(ChromiumDragAreaAction.ToggleMaximize, default));
            Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
        });
    }

    // Never with a real button: a loop started here would follow the pointer. Each refusal returns before it.
    [Fact]
    public void A_drag_is_refused_with_the_button_up_or_the_window_maximized_its_own_way()
    {
        Sta.Run(() =>
        {
            using var frameless = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(0, 0, 800, 600),
                ShowInTaskbar = false,
            };
            _ = frameless.Handle;

            Assert.False(FormCaption.Move(frameless, new Point(10, 10), buttonDown: () => false));
            Assert.False(FormCaption.Resize(frameless, 12 /* HTTOP */, buttonDown: () => false));

            frameless.ToggleMaximize();
            Assert.True(FormCaption.ManuallyMaximized(frameless));
            Assert.False(FormCaption.Move(frameless, new Point(10, 10), buttonDown: () => true));

            // A window the system maximized has true restore bounds: the system's own drag restores it.
            using var framed = new Form { WindowState = FormWindowState.Maximized, ShowInTaskbar = false };
            _ = framed.Handle;
            Assert.False(FormCaption.ManuallyMaximized(framed));
        });
    }

    // Posted from the view's own thread, the work runs inline, in the caller's context: the mark must end with it.
    [Fact]
    public void Work_posted_from_the_views_own_thread_runs_as_its_page_and_leaves_no_mark()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            var page = new Control();
            form.Controls.Add(page);
            _ = form.Handle;
            _ = page.Handle;
            var ui = new PageUiDispatcher(page);
            Control? during = null;

            Assert.True(ui.Post(() => during = PageSender.Current));

            Assert.Same(page, during);
            Assert.False(PageSender.IsPage(out _));
        });
    }

    [Fact]
    public void UseChromiumEngine_registers_one_engine_in_either_order_with_UseWindows()
    {
        var builder = Builder();
        builder.UseChromiumEngine(new ChromiumEngineOptions());
        builder.UseWindows(new WindowsHostOptions { MainForm = _ => new Form() });
        builder.UseChromiumEngine(new ChromiumEngineOptions());
        using var app = builder.Build();

        Assert.Single(app.Services.GetServices<ChromiumEngine>());
    }

    [Fact]
    public void An_app_with_the_engine_but_no_CEF_stops_before_building_its_form_naming_the_package()
    {
        var built = false;
        var builder = Builder();
        builder.UseChromiumEngine(new ChromiumEngineOptions());
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => { built = true; return new Form(); },
            SkipProcessInit = true,
            MessageLoop = _ => Assert.Fail("the loop must not run without CEF"),
        });
        using var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(app.Run);

        Assert.Contains("Reference the Shenora.Chromium package", error.Message);
        Assert.False(built);
    }
}
