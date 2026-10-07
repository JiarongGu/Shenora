using System.Text.Json;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The app's colour scheme: the setting both desktop shells register (<see cref="ColorSchemeState"/>), and the page's
/// route to it (<see cref="ColorSchemeModule"/>).
/// </summary>
public class ColorSchemeModuleTests
{
    [Fact]
    public void The_setting_starts_where_it_is_given_and_follows_the_system_by_default()
    {
        Assert.Equal(ColorScheme.System, new ColorSchemeState().Scheme);
        Assert.Equal(ColorScheme.Dark, new ColorSchemeState(ColorScheme.Dark).Scheme);
    }

    [Fact]
    public void A_change_is_raised_once_and_setting_the_same_value_raises_nothing()
    {
        var setting = new ColorSchemeState();
        var raised = new List<ColorScheme>();
        setting.Changed += raised.Add;

        setting.Set(ColorScheme.Light);
        setting.Set(ColorScheme.Light);
        setting.Set(ColorScheme.System);

        Assert.Equal([ColorScheme.Light, ColorScheme.System], raised);
        Assert.Equal(ColorScheme.System, setting.Scheme);
    }

    [Fact]
    public void A_value_the_enum_does_not_have_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorSchemeState((ColorScheme)7));
        var setting = new ColorSchemeState();
        Assert.Throws<ArgumentOutOfRangeException>(() => setting.Set((ColorScheme)7));
        Assert.Equal(ColorScheme.System, setting.Scheme);
    }

    [Fact]
    public void A_handler_that_throws_neither_fails_the_change_nor_keeps_it_from_the_others()
    {
        // The shells apply the setting from this event, after whatever the app subscribed: one app handler's fault
        // must not leave the browser engine on the old scheme.
        var setting = new ColorSchemeState();
        var reached = new List<ColorScheme>();
        setting.Changed += _ => throw new InvalidOperationException("an app's handler failed");
        setting.Changed += reached.Add;

        setting.Set(ColorScheme.Dark);

        Assert.Equal([ColorScheme.Dark], reached);
        Assert.Equal(ColorScheme.Dark, setting.Scheme);
    }

    private static IpcRequest Request(string type, object? payload = null) => new()
    {
        Id = "r1",
        Module = ColorSchemeModule.Module,
        Type = type,
        Payload = payload is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
    };

    private static async Task<IpcResponse> DispatchAsync(IColorScheme setting, IpcRequest request)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new ColorSchemeModule(setting));
        return await dispatcher.DispatchAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task The_page_reads_the_setting_and_sets_it()
    {
        var setting = new ColorSchemeState(ColorScheme.Light);

        var read = await DispatchAsync(setting, Request(ColorSchemeModule.GetSchemeType));
        Assert.True(read.Success);
        Assert.Equal("{\"scheme\":\"light\"}", JsonSerializer.Serialize(read.Data, IpcJson.Options));

        Assert.True((await DispatchAsync(setting, Request(ColorSchemeModule.SetSchemeType, new { scheme = "dark" }))).Success);
        Assert.Equal(ColorScheme.Dark, setting.Scheme);
    }

    [Fact]
    public async Task A_scheme_the_enum_does_not_have_or_none_at_all_is_refused_at_the_boundary()
    {
        var setting = new ColorSchemeState();

        var unknown = await DispatchAsync(setting, Request(ColorSchemeModule.SetSchemeType, new { scheme = "sepia" }));
        Assert.False(unknown.Success);
        Assert.Equal(IpcErrorCodes.InvalidPayloadValue, unknown.Error!.Code);

        // The wire spells an enum as its name: a number, defined or not, is refused as the name would be.
        foreach (var number in new object[] { 7, 2 })
        {
            var numeric = await DispatchAsync(setting, Request(ColorSchemeModule.SetSchemeType, new { scheme = number }));
            Assert.False(numeric.Success);
            Assert.Equal(IpcErrorCodes.InvalidPayloadValue, numeric.Error!.Code);
        }

        // An enum's default is a real value: a missing key would otherwise set the app to follow the system.
        var missing = await DispatchAsync(setting, Request(ColorSchemeModule.SetSchemeType, new { }));
        Assert.False(missing.Success);
        Assert.Equal(IpcErrorCodes.MissingPayloadValue, missing.Error!.Code);

        Assert.Equal(ColorScheme.System, setting.Scheme);
    }
}
