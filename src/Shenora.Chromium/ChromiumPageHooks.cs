using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium;

/// <summary>A download the page started, offered to <see cref="ChromiumHostOptions.OnDownloadStarting"/>.</summary>
public sealed class ChromiumDownloadRequest
{
    internal ChromiumDownloadRequest(string url, string suggestedName, string mimeType)
    {
        Url = url;
        SuggestedName = suggestedName;
        MimeType = mimeType;
    }

    /// <summary>What is being downloaded.</summary>
    public string Url { get; }

    /// <summary>The file name Chromium suggests, from the response or the link; empty when it has none.</summary>
    public string SuggestedName { get; }

    /// <summary>The content type the server named; empty when it named none.</summary>
    public string MimeType { get; }

    /// <summary>
    /// Where to save it: the full path, folder and file name, which Chromium writes without asking the user. Null cancels
    /// the download.
    /// </summary>
    public string? ResultFilePath { get; set; }
}

/// <summary>
/// What a page can ask Chromium for, as the flags of <see cref="ChromiumPermissionRequest.Kinds"/>; a request names one or
/// more. The values are CEF's.
/// </summary>
[Flags]
public enum ChromiumPermissionKind
{
    /// <summary>No kind.</summary>
    None = 0,
    /// <summary>An augmented-reality session.</summary>
    ArSession = 1 << 0,
    /// <summary>Camera pan, tilt and zoom.</summary>
    CameraPanTiltZoom = 1 << 1,
    /// <summary>The camera's video.</summary>
    CameraStream = 1 << 2,
    /// <summary>Control of a captured surface.</summary>
    CapturedSurfaceControl = 1 << 3,
    /// <summary>The clipboard's contents (a read; a write needs no permission).</summary>
    Clipboard = 1 << 4,
    /// <summary>Top-level storage access.</summary>
    TopLevelStorageAccess = 1 << 5,
    /// <summary>More disk quota.</summary>
    DiskQuota = 1 << 6,
    /// <summary>The system's fonts.</summary>
    LocalFonts = 1 << 7,
    /// <summary>The position.</summary>
    Geolocation = 1 << 8,
    /// <summary>Hand tracking.</summary>
    HandTracking = 1 << 9,
    /// <summary>The identity provider.</summary>
    IdentityProvider = 1 << 10,
    /// <summary>Whether the user is idle.</summary>
    IdleDetection = 1 << 11,
    /// <summary>The microphone.</summary>
    MicStream = 1 << 12,
    /// <summary>MIDI with system-exclusive messages.</summary>
    MidiSysex = 1 << 13,
    /// <summary>More than one download.</summary>
    MultipleDownloads = 1 << 14,
    /// <summary>Notifications.</summary>
    Notifications = 1 << 15,
    /// <summary>Keyboard lock.</summary>
    KeyboardLock = 1 << 16,
    /// <summary>Pointer lock.</summary>
    PointerLock = 1 << 17,
    /// <summary>A protected-media identifier.</summary>
    ProtectedMediaIdentifier = 1 << 18,
    /// <summary>Registering a protocol handler.</summary>
    RegisterProtocolHandler = 1 << 19,
    /// <summary>Storage access.</summary>
    StorageAccess = 1 << 20,
    /// <summary>A virtual-reality session.</summary>
    VrSession = 1 << 21,
    /// <summary>Installing the page as an app.</summary>
    WebAppInstallation = 1 << 22,
    /// <summary>Window management.</summary>
    WindowManagement = 1 << 23,
    /// <summary>File-system access.</summary>
    FileSystemAccess = 1 << 24,
    /// <summary>Local-network access (Chromium's earlier name for it).</summary>
    LocalNetworkAccessDeprecated = 1 << 25,
    /// <summary>Local-network access.</summary>
    LocalNetwork = 1 << 26,
    /// <summary>Loopback-network access.</summary>
    LoopbackNetwork = 1 << 27,
    /// <summary>The device's sensors.</summary>
    Sensors = 1 << 28,
}

/// <summary>A permission prompt the page raised, offered to <see cref="ChromiumHostOptions.OnPermissionRequested"/>.</summary>
public sealed class ChromiumPermissionRequest
{
    internal ChromiumPermissionRequest(string origin, ChromiumPermissionKind kinds)
    {
        Origin = origin;
        Kinds = kinds;
    }

    /// <summary>The origin that asked, such as <c>https://app.local</c>.</summary>
    public string Origin { get; }

    /// <summary>What it asked for.</summary>
    public ChromiumPermissionKind Kinds { get; }

    /// <summary>Whether to grant it. False until set.</summary>
    public bool Allow { get; set; }
}

/// <summary>How a page's renderer process ended.</summary>
public enum ChromiumProcessStatus
{
    /// <summary>It ended abnormally.</summary>
    Abnormal,
    /// <summary>Something killed it.</summary>
    Killed,
    /// <summary>It crashed.</summary>
    Crashed,
    /// <summary>It ran out of memory.</summary>
    OutOfMemory,
    /// <summary>It could not start.</summary>
    LaunchFailed,
    /// <summary>It failed a code-integrity check.</summary>
    IntegrityFailure,
}

/// <summary>A page's renderer process that ended, offered to <see cref="ChromiumHostOptions.OnProcessFailed"/>.</summary>
public sealed class ChromiumProcessReport
{
    internal ChromiumProcessReport(ChromiumProcessStatus status, int errorCode)
    {
        Status = status;
        ErrorCode = errorCode;
    }

    /// <summary>How it ended.</summary>
    public ChromiumProcessStatus Status { get; }

    /// <summary>The platform's exit code, or 0 when there is none.</summary>
    public int ErrorCode { get; }
}

/// <summary>
/// The app's answers to what a page asks of the shell (<see cref="ChromiumHostOptions.OnDownloadStarting"/>,
/// <see cref="ChromiumHostOptions.OnPermissionRequested"/>, <see cref="ChromiumHostOptions.OnProcessFailed"/>), and the
/// shell's default for each when the app has none or declines. A hook that throws is logged and declines. CEF's UI thread.
/// </summary>
internal sealed class ChromiumPageHooks(Func<ChromiumDownloadRequest, bool>? download, Func<ChromiumPermissionRequest, bool>? permission,
    Func<ChromiumProcessReport, bool>? failed)
{
    /// <summary>The hooks the app set, or null when it set none.</summary>
    public static ChromiumPageHooks? From(ChromiumHostOptions options) =>
        From(options.OnDownloadStarting, options.OnPermissionRequested, options.OnProcessFailed);

    /// <summary>The hooks the engine's app set, or null when it set none.</summary>
    public static ChromiumPageHooks? From(ChromiumEngineOptions options) =>
        From(options.OnDownloadStarting, options.OnPermissionRequested, options.OnProcessFailed);

    private static ChromiumPageHooks? From(Func<ChromiumDownloadRequest, bool>? download, Func<ChromiumPermissionRequest, bool>? permission,
        Func<ChromiumProcessReport, bool>? failed) =>
        download is null && permission is null && failed is null ? null : new ChromiumPageHooks(download, permission, failed);

    public bool HandlesDownloads => download is not null;

    /// <summary>The path the app chose for the download, or null: it cancelled it, or declined and the default (cancel) stands.</summary>
    public string? Download(string url, string suggestedName, string mimeType, ILogger? log)
    {
        if (download is null) return null;
        var starting = new ChromiumDownloadRequest(url, suggestedName, mimeType);
        var handled = AppCallback.RunOrDefault(() => download(starting), false, Failed(log, nameof(ChromiumHostOptions.OnDownloadStarting)));
        return handled && !string.IsNullOrWhiteSpace(starting.ResultFilePath) ? starting.ResultFilePath : null;
    }

    /// <summary>The app's answer to the prompt, or null when it declined (or set no hook) and the default policy answers.</summary>
    public bool? Permission(uint requested, string origin, ILogger? log)
    {
        if (permission is null) return null;
        var request = new ChromiumPermissionRequest(origin, (ChromiumPermissionKind)requested);
        var handled = AppCallback.RunOrDefault(() => permission(request), false, Failed(log, nameof(ChromiumHostOptions.OnPermissionRequested)));
        return handled ? request.Allow : null;
    }

    /// <summary>True when the app dealt with the renderer's end itself, which skips the shell's own reload.</summary>
    public bool ProcessFailed(cef_termination_status_t status, int errorCode, ILogger? log)
    {
        if (failed is null) return false;
        var failure = new ChromiumProcessReport(ProcessStatus(status), errorCode);
        return AppCallback.RunOrDefault(() => failed(failure), false, Failed(log, nameof(ChromiumHostOptions.OnProcessFailed)));
    }

    internal static ChromiumProcessStatus ProcessStatus(cef_termination_status_t status) => status switch
    {
        cef_termination_status_t.TS_PROCESS_WAS_KILLED => ChromiumProcessStatus.Killed,
        cef_termination_status_t.TS_PROCESS_CRASHED => ChromiumProcessStatus.Crashed,
        cef_termination_status_t.TS_PROCESS_OOM => ChromiumProcessStatus.OutOfMemory,
        cef_termination_status_t.TS_LAUNCH_FAILED => ChromiumProcessStatus.LaunchFailed,
        cef_termination_status_t.TS_INTEGRITY_FAILURE => ChromiumProcessStatus.IntegrityFailure,
        _ => ChromiumProcessStatus.Abnormal,
    };

    private static Action<Exception> Failed(ILogger? log, string hook) =>
        ex => AppCallback.Log(log, () => $"[Shenora.Chromium] {hook} threw; the shell's default applies", LogLevel.Warning, ex);
}
