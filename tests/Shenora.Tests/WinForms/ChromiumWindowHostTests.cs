using Shenora.Chromium;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The host over real own-thread pumps, with an engine that is not running (the test output has no CEF runtime): the page
/// opens nothing, and what is proven is everything around it: the window, its thread, its chrome, its remembered geometry.
/// The working page is the Chromium probes' (D83).
/// </summary>
public class ChromiumWindowHostTests
{
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(20);
        }
    }

    // A window that never shows: minimized and out of the taskbar, as the registry's own tests make theirs.
    private static void Hide(Form form)
    {
        form.ShowInTaskbar = false;
        form.WindowState = FormWindowState.Minimized;
    }

    private static ChromiumEngine Engine() => new(new ChromiumEngineOptions());

    private static ChromiumWindowHost Host(SecondaryWindows windows, string stateFolder, Func<string, ChromiumFormOptions>? describe = null) =>
        new(Engine(), windows, stateFolder, describe ?? (_ => new ChromiumFormOptions { Created = Hide }));

    [Fact]
    public void A_window_is_made_on_its_own_sta_thread_holding_a_view_at_the_route_it_was_given()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);

        Assert.True(host.Open("monitor", "/?window=monitor"));

        WaitUntil(() => host.FormNamed("monitor") is { IsHandleCreated: true }, "window creation");
        var form = host.FormNamed("monitor")!;
        var view = form.Controls.OfType<ChromiumView>().Single();
        Assert.Equal("/?window=monitor", view.Path);
        Assert.Equal(DockStyle.Fill, view.Dock);
        Assert.Equal("monitor", form.Name);
        Assert.Equal("monitor", form.Text); // untitled: its name
        Assert.Equal(["monitor"], host.Opened);

        host.Close("monitor");
        WaitUntil(() => !windows.HasWindow("monitor"), "window close");
    }

    [Fact]
    public void The_app_gives_the_chrome_and_hears_of_the_window_on_its_thread_before_it_shows()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var openerThread = Environment.CurrentManagedThreadId;
        int? createdThread = null;
        ApartmentState? apartment = null;
        bool? visibleThen = null;
        var host = Host(windows, state.Root, name => new ChromiumFormOptions
        {
            Title = "Title of " + name,
            Chrome = new OptimizedFormOptions { BackColor = Color.FromArgb(10, 20, 30), FramelessChrome = true },
            Created = form =>
            {
                createdThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                visibleThen = form.Visible;
                Hide(form);
            },
        });

        host.Open("w", "/");

        WaitUntil(() => host.FormNamed("w") is { IsHandleCreated: true }, "window creation");
        var form = host.FormNamed("w")!;
        Assert.NotEqual(openerThread, createdThread);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.False(visibleThen);
        Assert.Equal("Title of w", form.Text);
        Assert.Equal(Color.FromArgb(10, 20, 30), form.BackColor);
        Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);

        host.Close("w");
        WaitUntil(() => !windows.HasWindow("w"), "window close");
    }

    [Fact]
    public void Opening_an_open_name_activates_it_and_makes_no_second_window()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);

        Assert.True(host.Open("a", "/a"));
        WaitUntil(() => host.FormNamed("a") is not null, "first window");

        Assert.False(host.Open("a", "/elsewhere"));

        Assert.Equal(1, windows.WindowCount);
        Assert.Equal("/a", host.FormNamed("a")!.Controls.OfType<ChromiumView>().Single().Path);
        windows.CloseAll();
    }

    [Fact]
    public void Opened_names_only_this_hosts_windows_and_drops_one_that_closed()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);
        // A window of the same registry that this host did not open is not its to report.
        windows.Open("other", new SecondaryWindowOptions { CreateForm = () => { var f = new Form(); Hide(f); return f; } });

        host.Open("b", "/b");
        host.Open("a", "/a");
        WaitUntil(() => host.FormNamed("a") is not null && host.FormNamed("b") is not null, "windows");

        Assert.Equal(["a", "b"], host.Opened);
        host.Close("a");
        WaitUntil(() => !windows.HasWindow("a"), "window close");
        Assert.Equal(["b"], host.Opened);
        Assert.Null(host.FormNamed("other"));
        windows.CloseAll();
    }

    [Fact]
    public void A_post_runs_on_the_windows_thread_without_blocking_the_caller()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);
        host.Open("w", "/");
        WaitUntil(() => host.FormNamed("w") is { IsHandleCreated: true }, "window creation");

        var ran = new ManualResetEventSlim();
        var windowThread = 0;
        var form = host.FormNamed("w")!;
        var expected = 0;
        form.Invoke(() => expected = Environment.CurrentManagedThreadId);

        var posted = host.Post("w", f =>
        {
            Assert.Same(form, f);
            windowThread = Environment.CurrentManagedThreadId;
            ran.Set();
        });

        Assert.True(posted);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(expected, windowThread);
        windows.CloseAll();
    }

    [Fact]
    public void A_post_to_a_window_that_is_not_open_is_false_and_nothing_wrong()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);

        Assert.False(host.Post("nobody", _ => Assert.Fail("ran")));
        host.Close("nobody");
        host.Activate("nobody");
    }

    [Fact]
    public void Geometry_is_remembered_per_name_in_a_file_the_name_cannot_collide_in()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);

        host.Open("session:laptop/a1", "/a");
        WaitUntil(() => host.FormNamed("session:laptop/a1") is { IsHandleCreated: true }, "window creation");
        host.Close("session:laptop/a1");
        WaitUntil(() => !windows.HasWindow("session:laptop/a1"), "window close");

        var file = Path.Combine(state.Root, "session%3Alaptop%2Fa1.json");
        WaitUntil(() => File.Exists(file), "geometry save");
        Assert.NotEqual(ChromiumWindowHost.StateFile("session:laptop/a1"), ChromiumWindowHost.StateFile("session:laptop-a1"));
        Assert.Equal("session%3Alaptop%2Fa1.json", ChromiumWindowHost.StateFile("session:laptop/a1"));
    }

    [Fact]
    public void Closing_a_window_disposes_its_view_so_the_page_stops_hearing_the_bus()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);
        host.Open("w", "/");
        WaitUntil(() => host.FormNamed("w") is { IsHandleCreated: true }, "window creation");
        var view = host.FormNamed("w")!.Controls.OfType<ChromiumView>().Single();

        host.Close("w");
        WaitUntil(() => !windows.HasWindow("w"), "window close");

        Assert.True(view.IsDisposed);
    }

    [Fact]
    public void An_app_hook_that_throws_leaves_no_phantom_window()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root, _ => new ChromiumFormOptions { Created = _ => throw new InvalidOperationException("hook") });

        host.Open("boom", "/");

        WaitUntil(() => !windows.HasWindow("boom"), "failed-creation cleanup");
        Assert.Empty(host.Opened);
    }

    [Fact]
    public void The_host_does_not_own_the_registry_it_runs_in()
    {
        using var windows = new SecondaryWindows();
        using var state = TempDir.Create();
        var host = Host(windows, state.Root);
        host.Open("w", "/");
        WaitUntil(() => host.FormNamed("w") is not null, "window creation");

        host.Close("w");
        WaitUntil(() => !windows.HasWindow("w"), "window close");

        // Still the app's to use: a registry the host had disposed would refuse this.
        Assert.True(host.Open("again", "/"));
        windows.CloseAll();
    }

    [Fact]
    public void Arguments_are_checked()
    {
        using var windows = new SecondaryWindows();
        Assert.Throws<ArgumentNullException>(() => new ChromiumWindowHost(null!, windows, "x"));
        Assert.Throws<ArgumentNullException>(() => new ChromiumWindowHost(Engine(), null!, "x"));
        Assert.Throws<ArgumentException>(() => new ChromiumWindowHost(Engine(), windows, ""));
        var host = new ChromiumWindowHost(Engine(), windows, "x");
        Assert.Throws<ArgumentException>(() => host.Open("", "/"));
    }
}
