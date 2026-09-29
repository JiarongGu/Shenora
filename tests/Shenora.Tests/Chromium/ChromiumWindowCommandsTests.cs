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
        Assert.Equal(WindowCommandModule.SetThemeType, ChromiumWindowCommands.SetThemeType);
        Assert.Equal(WindowCommandModule.ShowSystemMenuType, ChromiumWindowCommands.ShowSystemMenuType);
    }

    [Fact]
    public async Task An_unwired_opt_in_route_answers_NO_ROUTE_as_the_WebView2_module_does()
    {
        // A window whose page draws its own caption buttons has nothing for a theme to change.
        var response = await Module().HandleMessageAsync(new IpcRequest { Id = "1", Module = ChromiumWindowCommands.Module, Type = "SET_THEME" });

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoRoute, response.Error?.Code);
    }

    [Fact]
    public async Task A_window_that_paints_its_caption_buttons_takes_the_pages_theme()
    {
        var window = Window(new ChromiumWindowOptions { NativeCaptionButtons = true });
        var module = new ChromiumWindowCommands(() => window);
        IpcRequest Theme(bool dark) => new()
        {
            Id = $"t{dark}", Module = ChromiumWindowCommands.Module, Type = "SET_THEME",
            Payload = System.Text.Json.JsonDocument.Parse($$"""{ "dark": {{(dark ? "true" : "false")}} }""").RootElement,
        };

        Assert.True((await module.HandleMessageAsync(Theme(dark: true))).Success);
        Assert.Same(CaptionButtonPalette.Dark, window.Theme);
        Assert.True((await module.HandleMessageAsync(Theme(dark: false))).Success);
        Assert.Same(CaptionButtonPalette.Light, window.Theme);

        // As the WebView2 shell answers it: `dark` is optional and defaults to true, so the same page works on both.
        var missing = await module.HandleMessageAsync(new IpcRequest { Id = "m", Module = ChromiumWindowCommands.Module, Type = "SET_THEME" });
        Assert.True(missing.Success);
        Assert.Same(CaptionButtonPalette.Dark, window.Theme);
    }

    [Fact]
    public void Painted_caption_buttons_on_a_framed_window_are_refused_where_the_caller_sees_it()
    {
        var framed = new ChromiumWindowOptions { NativeCaptionButtons = true, FramelessChrome = false };

        var refused = Assert.Throws<ArgumentException>(() => framed.Validate("options"));
        Assert.Contains(nameof(ChromiumWindowOptions.FramelessChrome), refused.Message);
        new ChromiumWindowOptions { NativeCaptionButtons = true }.Validate("options");   // frameless by default
    }

    [Fact]
    public async Task With_no_native_window_yet_every_command_is_a_safe_no_op()
    {
        var module = Module();

        // SET_CAPTION_BUTTONS and SHOW_SYSTEM_MENU are wired on Windows, the OS this suite runs on, since the shell owns
        // the window.
        foreach (var type in new[] { "MINIMIZE", "TOGGLE_MAXIMIZE", "CLOSE", "START_DRAG", "START_RESIZE", "SET_CAPTION_BUTTONS", "SHOW_SYSTEM_MENU" })
            Assert.True((await module.HandleMessageAsync(new IpcRequest { Id = type, Module = ChromiumWindowCommands.Module, Type = type })).Success, type);

        var maximized = await module.HandleMessageAsync(new IpcRequest { Id = "m", Module = ChromiumWindowCommands.Module, Type = "IS_MAXIMIZED" });
        Assert.Contains("\"maximized\":false", IpcJson.Serialize(maximized.Data), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A window with no CEF behind it: its commands have nothing to act on, which is the case under test.</summary>
    private static ChromiumWindowCommands Module()
    {
        var window = Window(new ChromiumWindowOptions());
        return new ChromiumWindowCommands(() => window);
    }

    private static ChromiumWindow Window(ChromiumWindowOptions options)
    {
        var origins = ChromiumOrigins.For("app.local", null, isDevelopment: false);
        var ui = new CefUiDispatcher(_ => true, () => true);
        return new ChromiumWindow("main", options, new ChromiumServing(null, origins, new ChromiumInterceptor()), origins,
            w => new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher() }, ui, _ => { }, (_, _) => true),
            _ => { }, null);
    }
}
