using System.Text.Json;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Serving;
using Shenora.Core.Ipc;
using Shenora.Windows;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <c>useDropZone</c> on the Chromium shell: the WebView2 module's routes, and the one this shell adds, over a
/// window with no CEF behind it. The paths CEF reports as a drag enters are handed in directly.
/// </summary>
public class ChromiumDropZonesTests
{
    /// <summary>A page's browser with no CEF behind it, and no window hosting it.</summary>
    internal static ChromiumBrowser Window(string name = "main")
    {
        var origins = ChromiumOrigins.For("app.local", null, isDevelopment: false);
        var ui = new CefUiDispatcher(_ => true, () => true);
        return new ChromiumBrowser(name, new ChromiumServing(null, origins, new ChromiumInterceptor()), origins,
            b => new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher() }, ui, _ => { }, (_, _) => true), null);
    }

    private static (ChromiumDropZones Module, ChromiumBrowser Window) Create()
    {
        var window = Window();
        return (new ChromiumDropZones(() => window), window);
    }

    private static Task<IpcResponse> Send(ChromiumDropZones module, string type, string zoneId) =>
        module.HandleMessageAsync(new IpcRequest
        {
            Id = type,
            Module = ChromiumDropZones.Module,
            Type = type,
            Payload = JsonSerializer.SerializeToElement(new { zoneId }),
        });

    private static string Json(IpcResponse response) => IpcJson.Serialize(response.Data);

    [Fact]
    public void The_routes_are_the_WebView2_ones()
    {
        Assert.Equal(DropZoneManager.Module, ChromiumDropZones.Module);
        Assert.Equal(DropZoneModule.RegisterType, ChromiumDropZones.RegisterType);
        Assert.Equal(DropZoneModule.UpdateType, ChromiumDropZones.UpdateType);
        Assert.Equal(DropZoneModule.UnregisterType, ChromiumDropZones.UnregisterType);
        Assert.Equal(DropZoneModule.ShowType, ChromiumDropZones.ShowType);
    }

    [Fact]
    public async Task Registering_says_the_page_delivers_the_drop()
    {
        var (module, _) = Create();

        Assert.Equal("""{"pageDrop":true}""", Json(await Send(module, "REGISTER", "z1")));
        Assert.Equal("""{"pageDrop":true}""", Json(await Send(module, "UPDATE", "z1")));
        Assert.True((await Send(module, "SHOW", "z1")).Success);
    }

    [Fact]
    public async Task A_drop_on_a_declared_zone_gets_the_drags_paths_once()
    {
        var (module, window) = Create();
        await Send(module, "REGISTER", "z1");
        window.FilesDraggedIn([@"C:\a.txt", @"C:\b c.txt"]);

        Assert.Equal("""{"files":["C:\\a.txt","C:\\b c.txt"]}""", Json(await Send(module, "DROP", "z1")));
        Assert.Equal("""{"files":[]}""", Json(await Send(module, "DROP", "z1")));   // no new drag, no paths
    }

    [Fact]
    public async Task A_drop_on_a_zone_the_page_never_declared_or_took_down_gets_nothing()
    {
        var (module, window) = Create();
        window.FilesDraggedIn([@"C:\a.txt"]);
        Assert.Equal("""{"files":[]}""", Json(await Send(module, "DROP", "nope")));

        await Send(module, "REGISTER", "z1");
        await Send(module, "UNREGISTER", "z1");
        Assert.Equal("""{"files":[]}""", Json(await Send(module, "DROP", "z1")));
    }

    [Fact]
    public async Task A_new_document_forgets_the_old_pages_zones()
    {
        var (module, window) = Create();
        await Send(module, "REGISTER", "z1");

        window.DocumentStarted();
        window.FilesDraggedIn([@"C:\a.txt"]);

        Assert.Equal("""{"files":[]}""", Json(await Send(module, "DROP", "z1")));
    }

    [Fact]
    public async Task One_module_keeps_each_windows_zones_and_drag_apart()
    {
        var main = Window("main");
        var second = Window("second");
        var module = new ChromiumDropZones(() => ChromiumBrowserContext.Current);

        using (ChromiumBrowserContext.Enter(main)) await Send(module, "REGISTER", "z1");
        main.FilesDraggedIn([@"C:\for-main.txt"]);
        second.FilesDraggedIn([@"C:\for-second.txt"]);

        // The second window never declared z1, so its drop gets nothing, and main's drag is untouched.
        using (ChromiumBrowserContext.Enter(second))
            Assert.Equal("""{"files":[]}""", Json(await Send(module, "DROP", "z1")));
        using (ChromiumBrowserContext.Enter(main))
            Assert.Equal("""{"files":["C:\\for-main.txt"]}""", Json(await Send(module, "DROP", "z1")));
    }

    [Theory]
    [InlineData("file:///C:/Users/x/drop-a.txt", true, true)]
    [InlineData("FILE:///C:/a.txt", true, true)]
    [InlineData("file:///C:/a.txt", false, false)]   // a subframe is the page's own business
    [InlineData("https://app.local/next.html", true, false)]
    [InlineData("https://example.com/", true, false)]
    public void The_main_frame_never_navigates_to_a_local_file(string url, bool mainFrame, bool refused) =>
        Assert.Equal(refused, ChromiumRouting.RefusesNavigation(url, mainFrame));
}
