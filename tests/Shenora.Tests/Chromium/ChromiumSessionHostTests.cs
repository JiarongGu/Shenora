using System.Drawing;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Sessions;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's session host (D91) where it needs no CEF: what it refuses before anything opens, where a profile
/// may be, and how it reads the DevTools protocol's answers into what the sessions expect of every shell. The browsers
/// themselves are measured against the Chromium sample.
/// </summary>
public class ChromiumSessionHostTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shenora-tests", "chromium-sessions");

    private static ChromiumSessionHost Host(bool offscreen = true)
    {
        // Nothing the refusals reach is ever posted; a post that ran would call CEF, which a test has none of.
        var ui = new CefUiDispatcher(_ => false, () => false);
        ui.MarkReady();
        return new ChromiumSessionHost(ui, Root, offscreen);
    }

    private static SessionBrowserDefinition Definition(SessionBrowserOptions options, string? visibleTitle = null) =>
        new() { Options = options, Scope = () => null, VisibleTitle = visibleTitle };

    // ── where a profile may be ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_profile_is_a_folder_directly_inside_the_data_folder()
    {
        var host = Host();
        Assert.Equal(Path.GetFullPath(Root), host.ProfilesDirectory);
        var profile = InteractiveSession.ComposeProfileDirectory(host.ProfilesDirectory, "provider.account");
        Assert.Equal(Path.GetFullPath(profile), host.ProfileFor(profile));
        Assert.Equal(Path.GetFullPath(profile), host.ProfileFor(profile + Path.DirectorySeparatorChar));
    }

    /// <summary>CEF opens any other path off the record, keeping nothing: refused rather than let lose every sign-in.</summary>
    [Fact]
    public void A_nested_profile_or_one_elsewhere_is_refused()
    {
        var host = Host();
        var nested = InteractiveSession.ComposeProfileDirectory(host.ProfilesDirectory, "provider", "account");
        Assert.Contains("directly inside", Assert.Throws<ArgumentException>(() => host.ProfileFor(nested)).Message);
        Assert.Throws<ArgumentException>(() => host.ProfileFor(Path.Combine(Path.GetTempPath(), "elsewhere")));
        Assert.Throws<ArgumentException>(() => host.ProfileFor(host.ProfilesDirectory));
        Assert.Throws<ArgumentException>(() => host.ProfileFor(Path.Combine(host.ProfilesDirectory, "a", "..", "..", "b")));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("Default")]
    public void The_apps_own_profile_is_refused_in_either_spelling(string name)
    {
        var host = Host();
        Assert.Contains("app's own profile", Assert.Throws<ArgumentException>(() => host.ProfileFor(Path.Combine(host.ProfilesDirectory, name))).Message);
    }

    // ── what it refuses before anything opens ─────────────────────────────────────────────────────────

    private sealed record OtherEngineOptions : SessionBrowserOptions;

    [Fact]
    public async Task It_refuses_what_it_cannot_honour_before_anything_opens()
    {
        var profile = Path.Combine(Root, "refusals");
        var plain = new SessionBrowserOptions { ProfileDirectory = profile };

        var notStarted = await Assert.ThrowsAsync<InvalidOperationException>(() => Host(offscreen: false).CreateAsync(Definition(plain), default));
        Assert.Contains(nameof(ChromiumHostOptions.OffscreenSessions), notStarted.Message);
        var other = new OtherEngineOptions { ProfileDirectory = profile };
        await Assert.ThrowsAsync<NotSupportedException>(() => Host().CreateAsync(Definition(other), default));
        // A window renders on screen, so it needs no off-screen rendering; what it cannot honour is refused all the same.
        await Assert.ThrowsAsync<NotSupportedException>(() => Host(offscreen: false).CreateAsync(Definition(other, visibleTitle: "watch"), default));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Host(offscreen: false).OpenWindowAsync(new SessionWindowDefinition { Options = other, Scope = () => null }, default));
        var nested = new SessionBrowserOptions { ProfileDirectory = Path.Combine(Root, "provider", "account") };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Host(offscreen: false).OpenWindowAsync(new SessionWindowDefinition { Options = nested, Scope = () => null }, default));
        Assert.False(Directory.Exists(profile));   // refused before the profile was made
    }

    /// <summary>An interactive window fitted to its page's content: the CSS box, within the display's work area less a
    /// margin for the frame, as the WinForms window fits.</summary>
    [Fact]
    public void A_window_fits_its_content_within_the_display()
    {
        Assert.Equal(new Size(500, 360), ChromiumSessionWindow.FitContent(500, 360, new Size(1920, 1040)));
        Assert.Equal(new Size(1880, 980), ChromiumSessionWindow.FitContent(3000, 3000, new Size(1920, 1040)));
        Assert.Equal(new Size(1, 1), ChromiumSessionWindow.FitContent(0, 0, new Size(20, 20)));
    }

    // ── registration ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shell_registers_it_over_its_data_folder()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Session host test" });
        var data = Path.Combine(Root, "data");
        builder.UseChromium(new ChromiumHostOptions { UserDataFolder = data, OffscreenSessions = true });
        using var app = builder.Build();
        var host = app.Services.GetRequiredService<ChromiumSessionHost>();
        Assert.Same(host, app.Services.GetRequiredService<ISessionHost>());
        Assert.Equal(Path.GetFullPath(data), host.ProfilesDirectory);
    }

    private sealed class OwnHost : ISessionHost
    {
        public Shenora.Core.Shell.IUiDispatcher Ui => throw new NotSupportedException();
        public ISessionBrowserContext CreateContext() => throw new NotSupportedException();
        public Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void An_apps_own_session_host_wins()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Session host test" });
        builder.Services.AddSingleton<ISessionHost, OwnHost>();
        builder.UseChromium(new ChromiumHostOptions());
        using var app = builder.Build();
        Assert.IsType<OwnHost>(app.Services.GetRequiredService<ISessionHost>());
    }

    // ── the protocol's answers, as every shell's sessions read them ──────────────────────────────────────

    [Theory]
    [InlineData("""{"result":{"type":"number","value":3,"description":"3"}}""", "3")]
    [InlineData("""{"result":{"type":"string","value":"a \"b\""}}""", "\"a \\u0022b\\u0022\"")]
    [InlineData("""{"result":{"type":"object","value":{"x":[1,2]}}}""", """{"x":[1,2]}""")]
    [InlineData("""{"result":{"type":"undefined"}}""", "null")]
    [InlineData("""{"result":{"type":"object","subtype":"error"},"exceptionDetails":{"text":"Uncaught"}}""", "null")]
    public void A_scripts_value_arrives_JSON_encoded_as_WebView2_answers(string answer, string expected)
    {
        var value = ChromiumSessionBrowser.ScriptResult(answer);
        // Compared as JSON: the protocol and the encoder may escape the same string differently.
        Assert.Equal(Normalize(expected), Normalize(value));

        static string Normalize(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
    }

    [Fact]
    public void Cookies_are_read_from_the_protocols_jar()
    {
        var cookies = ChromiumSessionBrowser.Cookies(
            """{"cookies":[{"name":"sid","value":"1","domain":"example.test","path":"/","secure":true},{"name":"bare"}]}""");
        Assert.Equal([new SessionCookie("sid", "1", "example.test", "/"), new SessionCookie("bare", "", "", "")], cookies);
        Assert.Empty(ChromiumSessionBrowser.Cookies("{}"));
    }

    /// <summary>A windowless browser's surface follows the emulated device, so a screencast frame is exactly what the
    /// page lays out in.</summary>
    [Fact]
    public void The_surface_takes_the_emulated_devices_size_and_scale()
    {
        Assert.Equal((new Size(800, 600), 2f),
            ChromiumSessionBrowser.EmulatedDevice("""{"width":800,"height":600,"deviceScaleFactor":2,"mobile":false}"""));
        Assert.Equal((new Size(390, 844), 1f), ChromiumSessionBrowser.EmulatedDevice("""{"width":390,"height":844,"deviceScaleFactor":0}"""));
        Assert.Null(ChromiumSessionBrowser.EmulatedDevice("""{"width":0,"height":0,"deviceScaleFactor":1}"""));   // the window's own
        Assert.Null(ChromiumSessionBrowser.EmulatedDevice("""{"deviceScaleFactor":2}"""));
    }

    [Fact]
    public void A_body_sample_is_the_first_characters_decoded()
    {
        Assert.Equal("hello", ChromiumSessionBrowser.BodySample("""{"body":"hello world","base64Encoded":false}""", 5));
        Assert.Equal("short", ChromiumSessionBrowser.BodySample("""{"body":"short","base64Encoded":false}""", 100));
        var encoded = Convert.ToBase64String("{\"ok\":true}"u8.ToArray());
        Assert.Equal("{\"ok\"", ChromiumSessionBrowser.BodySample($$"""{"body":"{{encoded}}","base64Encoded":true}""", 5));
    }

    [Fact]
    public void A_challenge_reads_as_WebView2_reports_it()
    {
        var (requestId, challenge) = ChromiumSessionBrowser.AuthChallenge(
            """{"requestId":"interception-7","request":{"url":"http://127.0.0.1:3911/auth","method":"GET"},"authChallenge":{"source":"Server","origin":"http://127.0.0.1:3911","scheme":"basic","realm":"probe"}}""");
        Assert.Equal("interception-7", requestId);
        Assert.Equal("http://127.0.0.1:3911/auth", challenge.Uri);
        Assert.Equal("Basic realm=\"probe\"", challenge.Challenge);
    }

    [Fact]
    public void Credentials_are_given_only_when_both_halves_are_set_and_cancel_otherwise()
    {
        var answered = new SessionAuthRequest("u", "c") { UserName = "user", Password = "secret" };
        using (var doc = JsonDocument.Parse(ChromiumSessionBrowser.AuthAnswer("r1", answered)))
        {
            var response = doc.RootElement.GetProperty("authChallengeResponse");
            Assert.Equal("r1", doc.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("ProvideCredentials", response.GetProperty("response").GetString());
            Assert.Equal("user", response.GetProperty("username").GetString());
            Assert.Equal("secret", response.GetProperty("password").GetString());
        }
        foreach (var partial in new[] { new SessionAuthRequest("u", "c"), new SessionAuthRequest("u", "c") { UserName = "user" } })
        {
            using var doc = JsonDocument.Parse(ChromiumSessionBrowser.AuthAnswer("r2", partial));
            var response = doc.RootElement.GetProperty("authChallengeResponse");
            Assert.Equal("CancelAuth", response.GetProperty("response").GetString());
            Assert.False(response.TryGetProperty("password", out _));
        }
    }

    /// <summary>Only a document's own context is tracked for posted messages, with its frame, so a frame's post is told
    /// from the top document's.</summary>
    [Fact]
    public void A_documents_main_world_context_is_known_by_its_frame()
    {
        Assert.Equal((3, "F00D"), ChromiumSessionBrowser.DefaultContext(
            """{"context":{"id":3,"origin":"http://127.0.0.1:3911","name":"","uniqueId":"x","auxData":{"isDefault":true,"type":"default","frameId":"F00D"}}}"""));
        Assert.Null(ChromiumSessionBrowser.DefaultContext(   // an isolated world
            """{"context":{"id":4,"name":"ext","auxData":{"isDefault":false,"type":"isolated","frameId":"F00D"}}}"""));
        Assert.Null(ChromiumSessionBrowser.DefaultContext("""{"context":{"id":5,"name":"worker"}}"""));
    }

    /// <summary>A hook written for one shell reads the same in the other: each kind by WebView2's name for it.</summary>
    [Fact]
    public void Permission_kinds_carry_WebView2s_names()
    {
        static string[] Kinds(IEnumerable<(uint Bit, string Kind)> kinds) => kinds.Select(k => k.Kind).ToArray();

        Assert.Equal(["Geolocation"], Kinds(ChromiumSessionBrowser.PermissionKinds((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION)));
        Assert.Equal(["Camera", "Microphone"], Kinds(ChromiumSessionBrowser.PermissionKinds(
            (uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM | cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM))));
        // In bit order.
        Assert.Equal(["ClipboardRead", "MidiSystemExclusiveMessages", "Notifications"], Kinds(ChromiumSessionBrowser.PermissionKinds(
            (uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD | cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS
                   | cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX))));
        // One WebView2 has no name for keeps CEF's.
        Assert.Equal(["CameraPanTiltZoom"], Kinds(ChromiumSessionBrowser.PermissionKinds((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM)));
        Assert.Empty(ChromiumSessionBrowser.PermissionKinds(0));
        Assert.Equal(["UnknownPermission"], Kinds(ChromiumSessionBrowser.PermissionKinds(1u << 31)));   // a bit CEF has no name for

        Assert.Equal([(1u, "Microphone"), (2u, "Camera")], ChromiumSessionBrowser.MediaKinds(
            (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE
                   | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE)));
        Assert.Equal(["DesktopVideoCapture"], Kinds(ChromiumSessionBrowser.MediaKinds(
            (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE)));
    }
}
