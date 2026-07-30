using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Real own-thread pumps drive these — assertions poll with a timeout because window threads run
/// asynchronously by design. Content behavior (WebView2 in a secondary window) is e2e territory.
/// </summary>
public class SecondaryWindowsTests
{
    private sealed class RecordingStore : IWindowStateStore
    {
        public WindowState? Saved { get; private set; }

        public WindowState? Load() => null;

        public void Save(WindowState state) => Saved = state;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Open_runs_the_window_on_its_own_sta_thread()
    {
        using var windows = new SecondaryWindows();
        var openerThread = Environment.CurrentManagedThreadId;
        int? windowThread = null;
        ApartmentState? apartment = null;

        var opened = windows.Open("w1", new SecondaryWindowOptions
        {
            CreateForm = () =>
            {
                windowThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                return new Form { Text = "w1", ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            },
        });

        Assert.True(opened);
        WaitUntil(() => windows.TryGetForm("w1") is { IsHandleCreated: true }, "window creation");
        Assert.NotEqual(openerThread, windowThread);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(windows.HasWindow("w1"));

        windows.Close("w1");
        WaitUntil(() => !windows.HasWindow("w1"), "window close");
    }

    [Fact]
    public void Opening_an_existing_name_returns_false()
    {
        using var windows = new SecondaryWindows();
        SecondaryWindowOptions Options() => new()
        {
            CreateForm = () => new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
        };

        Assert.True(windows.Open("w1", Options()));
        WaitUntil(() => windows.TryGetForm("w1") is not null, "first window");

        Assert.False(windows.Open("w1", Options())); // activates the existing one instead
        Assert.Equal(1, windows.WindowCount);
    }

    [Fact]
    public void Close_before_the_form_exists_still_tears_down()
    {
        using var windows = new SecondaryWindows();
        var gate = new ManualResetEventSlim();

        windows.Open("slow", new SecondaryWindowOptions
        {
            CreateForm = () =>
            {
                gate.Wait(TimeSpan.FromSeconds(10)); // hold creation until Close raced past
                return new Form { ShowInTaskbar = false };
            },
        });

        windows.Close("slow"); // form doesn't exist yet — must not be lost
        gate.Set();

        WaitUntil(() => !windows.HasWindow("slow"), "raced close");
    }

    [Fact]
    public void Geometry_saves_through_the_state_store_on_close()
    {
        using var windows = new SecondaryWindows();
        var store = new RecordingStore();

        windows.Open("w1", new SecondaryWindowOptions
        {
            CreateForm = () => new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
            StateStore = store,
        });
        WaitUntil(() => windows.TryGetForm("w1") is { IsHandleCreated: true }, "window creation");

        windows.Close("w1");
        WaitUntil(() => !windows.HasWindow("w1"), "window close");
        WaitUntil(() => store.Saved is not null, "state save");
        Assert.True(store.Saved!.Width > 0);
    }

    [Fact]
    public void A_failing_factory_cleans_its_registry_entry()
    {
        using var windows = new SecondaryWindows();

        windows.Open("boom", new SecondaryWindowOptions
        {
            CreateForm = () => throw new InvalidOperationException("factory failed"),
        });

        WaitUntil(() => !windows.HasWindow("boom"), "failed-factory cleanup");
    }

    [Fact]
    public void Dispose_waits_for_the_pumps_so_saves_run()
    {
        // Regression: dispose used to fire-and-forget the closes; background pumps died with
        // the process before FormClosed-driven geometry saves ran.
        var windows = new SecondaryWindows();
        var store = new RecordingStore();
        windows.Open("w1", new SecondaryWindowOptions
        {
            CreateForm = () => new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
            StateStore = store,
        });
        WaitUntil(() => windows.TryGetForm("w1") is { IsHandleCreated: true }, "window creation");

        windows.Dispose(); // must return only after the pump drained (bounded)

        Assert.Equal(0, windows.WindowCount);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public void CloseAll_empties_the_registry()
    {
        using var windows = new SecondaryWindows();
        SecondaryWindowOptions Options() => new()
        {
            CreateForm = () => new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
        };
        windows.Open("a", Options());
        windows.Open("b", Options());
        WaitUntil(() => windows.TryGetForm("a") is not null && windows.TryGetForm("b") is not null, "both windows");

        windows.CloseAll();

        WaitUntil(() => windows.WindowCount == 0, "close all");
    }
}
