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

/// <summary>Where a download stands.</summary>
public enum ChromiumDownloadState
{
    /// <summary>Still arriving.</summary>
    InProgress,
    /// <summary>Finished: the file is complete at <see cref="ChromiumDownloadProgress.FilePath"/>.</summary>
    Complete,
    /// <summary>Canceled, by the user or the shell; the file is not complete.</summary>
    Canceled,
    /// <summary>Stopped by an error (the network, the disk); the file is not complete.</summary>
    Interrupted,
}

/// <summary>
/// A download changed, offered to <see cref="ChromiumHostOptions.OnDownloadProgress"/>. The last one of a download is not
/// <see cref="ChromiumDownloadState.InProgress"/>.
/// </summary>
public sealed class ChromiumDownloadProgress
{
    private readonly IChromiumDownloadControl? _control;

    internal ChromiumDownloadProgress(uint id, string url, string filePath, ChromiumDownloadState state, long receivedBytes, long totalBytes,
        bool isPaused = false, IChromiumDownloadControl? control = null)
    {
        Id = id;
        Url = url;
        FilePath = filePath;
        State = state;
        ReceivedBytes = receivedBytes;
        TotalBytes = totalBytes;
        IsPaused = isPaused;
        _control = control;
    }

    /// <summary>Tells one download's updates from another's, for this run of the app.</summary>
    public uint Id { get; }

    /// <summary>What is being downloaded.</summary>
    public string Url { get; }

    /// <summary>The full path it is written to; empty before the app's <see cref="ChromiumHostOptions.OnDownloadStarting"/> has named one, and for a download it cancelled.</summary>
    public string FilePath { get; }

    /// <summary>Where it stands.</summary>
    public ChromiumDownloadState State { get; }

    /// <summary>The bytes written so far.</summary>
    public long ReceivedBytes { get; }

    /// <summary>The size the server announced, or -1 when it announced none: never read -1 as zero.</summary>
    public long TotalBytes { get; }

    /// <summary>Whether it is paused: <see cref="State"/> stays <see cref="ChromiumDownloadState.InProgress"/> while it is.</summary>
    public bool IsPaused { get; }

    /// <summary>Cancels the download: its next update is <see cref="ChromiumDownloadState.Canceled"/>. Any thread; nothing once it has ended.</summary>
    public void Cancel() => _control?.Cancel();

    /// <summary>Pauses the download; <see cref="IsPaused"/> reads true on its next update. Any thread; nothing once it has ended.</summary>
    public void Pause() => _control?.Pause();

    /// <summary>Resumes a paused download. Any thread; nothing once it has ended.</summary>
    public void Resume() => _control?.Resume();
}

/// <summary>What the app can do to a download in flight, from CEF's item callback.</summary>
internal interface IChromiumDownloadControl
{
    void Cancel();
    void Pause();
    void Resume();
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
/// A certificate error the page's navigation met, offered to <see cref="ChromiumHostOptions.OnCertificateError"/>. The navigation
/// fails unless the app sets <see cref="Allow"/>.
/// </summary>
public sealed class ChromiumCertificateError
{
    internal ChromiumCertificateError(string url, int errorCode, string subject, string issuer, string sha256)
    {
        Url = url;
        ErrorCode = errorCode;
        Subject = subject;
        Issuer = issuer;
        Sha256 = sha256;
    }

    /// <summary>The address the page navigated to.</summary>
    public string Url { get; }

    /// <summary>Chromium's net error code, such as -202 for an authority it does not trust.</summary>
    public int ErrorCode { get; }

    /// <summary>The error's name in Chromium's list, such as <c>ERR_CERT_AUTHORITY_INVALID</c>.</summary>
    public string ErrorName => ChromiumPageHooks.ErrorName(ErrorCode);

    /// <summary>The server certificate's subject, as Chromium displays it; empty when there is none.</summary>
    public string Subject { get; }

    /// <summary>The server certificate's issuer, as Chromium displays it; empty when there is none.</summary>
    public string Issuer { get; }

    /// <summary>The SHA-256 of the server certificate's DER bytes, as 64 lowercase hex digits; empty when there is no certificate. What an app pins.</summary>
    public string Sha256 { get; }

    /// <summary>Whether to carry on with the navigation despite the error. False until set.</summary>
    public bool Allow { get; set; }
}

/// <summary>What a file dialog asks for: a page's <c>&lt;input type="file"&gt;</c> and its folder variant.</summary>
public enum ChromiumFileDialogMode
{
    /// <summary>One existing file.</summary>
    Open,
    /// <summary>One or more existing files.</summary>
    OpenMultiple,
    /// <summary>One existing folder.</summary>
    OpenFolder,
    /// <summary>A file to be written, which may not exist yet.</summary>
    Save,
}

/// <summary>A file dialog the page asked for, offered to <see cref="ChromiumHostOptions.OnFileDialog"/>.</summary>
public sealed class ChromiumFileDialogRequest
{
    internal ChromiumFileDialogRequest(ChromiumFileDialogMode mode, string title, string defaultPath, IReadOnlyList<string> accept)
    {
        Mode = mode;
        Title = title;
        DefaultPath = defaultPath;
        Accept = accept;
    }

    /// <summary>Which kind of dialog the page asked for.</summary>
    public ChromiumFileDialogMode Mode { get; }

    /// <summary>The title Chromium would give the dialog; empty when it has none.</summary>
    public string Title { get; }

    /// <summary>The folder or file the dialog would start at; empty when Chromium names none.</summary>
    public string DefaultPath { get; }

    /// <summary>What the page's <c>accept</c> allows: <c>.ext</c> and MIME patterns (<c>image/*</c>), with the extensions Chromium adds for a MIME type; empty when any file will do.</summary>
    public IReadOnlyList<string> Accept { get; }

    /// <summary>The full paths the page is given: none cancels the dialog, as a user's cancel does. Empty until set.</summary>
    public IReadOnlyList<string> Paths { get; set; } = [];
}

/// <summary>
/// An HTTP authentication challenge the page met (a server's <c>401</c> or a proxy's <c>407</c>), offered to
/// <see cref="ChromiumHostOptions.OnAuthRequest"/>.
/// </summary>
public sealed class ChromiumAuthRequest
{
    internal ChromiumAuthRequest(string url, string origin, string scheme, string realm, bool isProxy)
    {
        Url = url;
        Origin = origin;
        Scheme = scheme;
        Realm = realm;
        IsProxy = isProxy;
    }

    /// <summary>The address that was requested.</summary>
    public string Url { get; }

    /// <summary>The origin that asked for credentials, such as <c>http://device.local</c>: the proxy's, for a proxy.</summary>
    public string Origin { get; }

    /// <summary>The scheme the challenge named, in lower case: <c>basic</c>, <c>digest</c>, <c>ntlm</c>.</summary>
    public string Scheme { get; }

    /// <summary>The realm the challenge named; empty when it named none.</summary>
    public string Realm { get; }

    /// <summary>True when a proxy asked, false when the server did.</summary>
    public bool IsProxy { get; }

    /// <summary>The user name to answer with. Set both halves to answer; leave them null to cancel the challenge.</summary>
    public string? UserName { get; set; }

    /// <summary>The password to answer with. Set both halves to answer; leave them null to cancel the challenge.</summary>
    public string? Password { get; set; }

    /// <summary>Redacted: this object holds a password, and a log line or a debugger watch formats it.</summary>
    public override string ToString() =>
        $"{nameof(ChromiumAuthRequest)} {{ Url = {Url}, Scheme = {Scheme}, Realm = {Realm}, IsProxy = {IsProxy}, "
        + $"UserName = {(UserName is null ? "null" : "***")}, Password = {(Password is null ? "null" : "***")} }}";
}

/// <summary>
/// The app's answers to what a page asks of the shell (<see cref="ChromiumHostOptions.OnDownloadStarting"/>,
/// <see cref="ChromiumHostOptions.OnPermissionRequested"/>, <see cref="ChromiumHostOptions.OnProcessFailed"/>,
/// <see cref="ChromiumHostOptions.OnCertificateError"/>, <see cref="ChromiumHostOptions.OnFileDialog"/>,
/// <see cref="ChromiumHostOptions.OnAuthRequest"/>, and
/// <see cref="ChromiumHostOptions.OnDownloadProgress"/>, which only observes), and the
/// shell's default for each when the app has none or declines. A hook that throws is logged and declines. CEF's UI thread.
/// </summary>
internal sealed class ChromiumPageHooks(Func<ChromiumDownloadRequest, bool>? download, Func<ChromiumPermissionRequest, bool>? permission,
    Func<ChromiumProcessReport, bool>? failed, Action<ChromiumDownloadProgress>? progress = null,
    Func<ChromiumCertificateError, bool>? certificate = null, Func<ChromiumFileDialogRequest, bool>? fileDialog = null,
    Func<ChromiumAuthRequest, bool>? auth = null)
{
    /// <summary>The hooks the app set, or null when it set none.</summary>
    public static ChromiumPageHooks? From(ChromiumHostOptions options) =>
        From(options.OnDownloadStarting, options.OnPermissionRequested, options.OnProcessFailed, options.OnDownloadProgress, options.OnCertificateError,
            options.OnFileDialog, options.OnAuthRequest);

    /// <summary>The hooks the engine's app set, or null when it set none.</summary>
    public static ChromiumPageHooks? From(ChromiumEngineOptions options) =>
        From(options.OnDownloadStarting, options.OnPermissionRequested, options.OnProcessFailed, options.OnDownloadProgress, options.OnCertificateError,
            options.OnFileDialog, options.OnAuthRequest);

    private static ChromiumPageHooks? From(Func<ChromiumDownloadRequest, bool>? download, Func<ChromiumPermissionRequest, bool>? permission,
        Func<ChromiumProcessReport, bool>? failed, Action<ChromiumDownloadProgress>? progress, Func<ChromiumCertificateError, bool>? certificate,
        Func<ChromiumFileDialogRequest, bool>? fileDialog, Func<ChromiumAuthRequest, bool>? auth) =>
        download is null && permission is null && failed is null && progress is null && certificate is null && fileDialog is null && auth is null
            ? null : new ChromiumPageHooks(download, permission, failed, progress, certificate, fileDialog, auth);

    /// <summary>Whether the app set an authentication hook: only then is a page's DevTools channel opened to hear its challenges.</summary>
    public bool HandlesAuth => auth is not null;

    /// <summary>
    /// The credentials the app answered the challenge with: both halves, or both null to cancel it. Null when the app declined, threw
    /// or set no hook, and the challenge goes on as it did before the hook (the page's <c>401</c> body draws).
    /// </summary>
    public (string? UserName, string? Password)? Authenticate(ChromiumAuthRequest request, ILogger? log)
    {
        if (auth is null) return null;
        var handled = AppCallback.RunOrDefault(() => auth(request), false, Failed(log, nameof(ChromiumHostOptions.OnAuthRequest)));
        if (!handled) return null;
        return request.UserName is { } user && request.Password is { } password ? (user, password) : (null, null);
    }

    /// <summary>Whether the app set a file dialog hook: without one CEF is not given a dialog handler and shows its own dialog.</summary>
    public bool HandlesFileDialogs => fileDialog is not null;

    /// <summary>The paths the app answered the dialog with (none cancels it), or null when it declined, threw or set no hook and CEF's own dialog stands.</summary>
    public string[]? FileDialog(ChromiumFileDialogMode mode, string title, string defaultPath, IReadOnlyList<string> accept, ILogger? log)
    {
        if (fileDialog is null) return null;
        var request = new ChromiumFileDialogRequest(mode, title, defaultPath, accept);
        var handled = AppCallback.RunOrDefault(() => fileDialog(request), false, Failed(log, nameof(ChromiumHostOptions.OnFileDialog)));
        if (!handled) return null;
        var paths = (request.Paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        // A single-file dialog takes one path whatever the app named.
        return mode == ChromiumFileDialogMode.OpenMultiple ? paths : paths.Take(1).ToArray();
    }

    /// <summary>Whether the app set a certificate hook: without one CEF is not asked, and its own policy (the navigation fails) stands.</summary>
    public bool HandlesCertificates => certificate is not null;

    /// <summary>True when the app allowed the navigation to go on despite the error. A throw, no hook, or an answer of false is the failure CEF would give.</summary>
    public bool CertificateError(string url, int errorCode, string subject, string issuer, string sha256, ILogger? log)
    {
        if (certificate is null) return false;
        var error = new ChromiumCertificateError(url, errorCode, subject, issuer, sha256);
        var handled = AppCallback.RunOrDefault(() => certificate(error), false, Failed(log, nameof(ChromiumHostOptions.OnCertificateError)));
        return handled && error.Allow;
    }

    internal static string ErrorName(int code) => Enum.IsDefined(typeof(cef_errorcode_t), code) ? ((cef_errorcode_t)code).ToString() : $"ERR_{code}";

    /// <summary>Whether the download handler is given to CEF: an app that watches downloads gets it too, and its downloads are
    /// still cancelled unless <see cref="ChromiumHostOptions.OnDownloadStarting"/> takes them.</summary>
    public bool HandlesDownloads => download is not null || progress is not null;

    /// <summary>Tells the app a download it took changed. A hook that throws is logged and ignored.</summary>
    public void DownloadUpdated(ChromiumDownloadProgress update, ILogger? log)
    {
        if (progress is null) return;
        AppCallback.Run(() => progress(update), Failed(log, nameof(ChromiumHostOptions.OnDownloadProgress)));
    }

    internal static ChromiumDownloadState DownloadState(bool canceled, bool complete, bool interrupted) =>
        canceled ? ChromiumDownloadState.Canceled
        : complete ? ChromiumDownloadState.Complete
        : interrupted ? ChromiumDownloadState.Interrupted
        : ChromiumDownloadState.InProgress;

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
