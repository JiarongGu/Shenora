using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Serving;
using Shenora.Core.Ipc;
using Shenora.Windows;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A page's title bar works unchanged on either engine only if the Chromium shell answers the WebView2 shell's
/// window routes by the same names, and refuses what is not wired in the same words.
/// </summary>
public class ChromiumWindowCommandsTests
{
    [Fact]
    public void The_Chromium_window_routes_are_the_WebView2_ones()
    {
        Assert.Equal(WindowCommandModule.Module, ChromiumWindowCommands.Module);
        Assert.Equal(WindowCommandModule.MinimizeType, ChromiumWindowCommands.MinimizeType);
        Assert.Equal(WindowCommandModule.ToggleMaximizeType, ChromiumWindowCommands.ToggleMaximizeType);
        Assert.Equal(WindowCommandModule.CloseType, ChromiumWindowCommands.CloseType);
        Assert.Equal(WindowCommandModule.IsMaximizedType, ChromiumWindowCommands.IsMaximizedType);
        Assert.Equal(WindowCommandModule.StartDragType, ChromiumWindowCommands.StartDragType);
        Assert.Equal(WindowCommandModule.StartResizeType, ChromiumWindowCommands.StartResizeType);
        Assert.Equal(WindowCommandModule.SetCaptionButtonsType, ChromiumWindowCommands.SetCaptionButtonsType);
    }

    [Fact]
    public async Task An_unwired_opt_in_route_answers_NO_ROUTE_as_the_WebView2_module_does()
    {
        var response = await Module().HandleMessageAsync(new IpcRequest { Id = "1", Module = ChromiumWindowCommands.Module, Type = "SET_THEME" });

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoRoute, response.Error?.Code);
    }

    [Fact]
    public async Task With_no_native_window_yet_every_command_is_a_safe_no_op()
    {
        var module = Module();

        // SET_CAPTION_BUTTONS is wired on Windows, the OS this suite runs on, since the shell owns the window.
        foreach (var type in new[] { "MINIMIZE", "TOGGLE_MAXIMIZE", "CLOSE", "START_DRAG", "START_RESIZE", "SET_CAPTION_BUTTONS" })
            Assert.True((await module.HandleMessageAsync(new IpcRequest { Id = type, Module = ChromiumWindowCommands.Module, Type = type })).Success, type);

        var maximized = await module.HandleMessageAsync(new IpcRequest { Id = "m", Module = ChromiumWindowCommands.Module, Type = "IS_MAXIMIZED" });
        Assert.Contains("\"maximized\":false", IpcJson.Serialize(maximized.Data), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A window with no CEF behind it: its commands have nothing to act on, which is the case under test.</summary>
    private static ChromiumWindowCommands Module()
    {
        var origins = ChromiumOrigins.For("app.local", null, isDevelopment: false);
        var ui = new CefUiDispatcher(_ => true, () => true);
        var window = new ChromiumWindow("main", new ChromiumWindowOptions(), new ChromiumServing(null, origins, new ChromiumInterceptor()), origins,
            w => new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher() }, ui, _ => { }, (_, _) => true),
            _ => { }, null);
        return new ChromiumWindowCommands(() => window);
    }
}
