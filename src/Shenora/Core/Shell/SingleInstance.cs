using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shenora.Core.Shell;

/// <summary>What <see cref="SingleInstanceGuard.TryAcquire()"/> found.</summary>
public enum SingleInstanceResult
{
    /// <summary>This process owns the scope — carry on starting.</summary>
    Acquired,

    /// <summary>Another live instance owns it. Ask it to come forward and exit; do not start.</summary>
    AlreadyRunning,

    /// <summary>
    /// The OS refused to answer and the guard FAILED OPEN — start, but single-instance is not enforced
    /// for this run. Distinct from <see cref="Acquired"/>: both mean "keep starting", but an app whose
    /// reason for being single-instance is a single-writer store may want to refuse or degrade.
    /// </summary>
    Unverified,
}

/// <summary>A later launch asking the running instance to come forward: its arguments, and the folder it started
/// in, against which a relative path among them resolves.</summary>
/// <param name="Arguments">The later launch's arguments, without the executable.</param>
/// <param name="WorkingDirectory">The later launch's working directory.</param>
public sealed record SingleInstanceLaunch(IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// Enforces ONE running instance per scope (normally the install directory), so distinct installs run
/// side-by-side, on every OS. A later launch calls <see cref="TryAcquire()"/>
/// (<see cref="SingleInstanceResult.AlreadyRunning"/>), then <see cref="ActivateRunning"/>, and exits; the running
/// instance, which called <see cref="Listen"/>, receives its arguments and comes to the front. The shells do all of
/// it (<see cref="SingleInstanceHostOptions"/>).
/// <para>
/// The scope is a named mutex limited to the user, and on Windows to the logon session as well, as it always was
/// there. On Linux and macOS it is not limited to the session, which is one terminal: two launches from two
/// terminals, or from a terminal and the desktop, are one user's two instances. The channel is a named pipe per
/// user (and per Windows session) that only the same user can open.
/// </para>
/// <para>
/// ⚠ Acquire FIRST at startup, before anything that takes an OS lock (the WebView2 environment prewarm, Chromium's
/// data folder).
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // A launch's arguments are small; this bounds what a connection can make the running instance read.
    private const int MaxMessageBytes = 256 * 1024;

    // Windows keeps the session scope it always had; elsewhere a session is one terminal (see the summary).
    private static readonly NamedWaitHandleOptions MutexScope = new()
    {
        CurrentUserOnly = true,
        CurrentSessionOnly = OperatingSystem.IsWindows(),
    };

    private readonly ILogger? _log;
    // Held for the process lifetime; the OS releases it at process exit either way.
    private Mutex? _mutex;
    private CancellationTokenSource? _listening;
    private Task? _listener;

    /// <param name="applicationName">Stable app identifier — names the mutex and the channel.</param>
    /// <param name="scope">
    /// What "one instance" is scoped to — normally the install directory (so two installs may run
    /// side-by-side). Null/empty scopes the guard to the application name alone.
    /// </param>
    /// <param name="log">Where a failure of the channel is reported. Null reports nothing.</param>
    public SingleInstanceGuard(string applicationName, string? scope = null, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationName);
        ApplicationName = applicationName;
        var key = ChannelKey(scope);
        MutexName = $"{applicationName.Replace('\\', '_').Replace('/', '_')}.instance.{key}";
        // Per user, and per session on Windows, as the mutex is: a pipe's name is the machine's (on Unix a socket in a
        // shared folder), so a name shared by two users' instances would let only the first of them listen. Short and
        // fixed-length: a macOS socket path holds 104 bytes, which a long application name would pass.
        ChannelName = $"shenora-{ChannelKey(applicationName)}{key}{ChannelKey(UserOf())}";
        _log = log;
    }

    /// <summary>The stable app identifier the names are derived from.</summary>
    public string ApplicationName { get; }

    /// <summary>The per-scope mutex name (exposed for tests/diagnostics).</summary>
    public string MutexName { get; }

    /// <summary>The pipe a later launch reaches the running instance through.</summary>
    internal string ChannelName { get; }

    /// <summary>How long a later launch's request may take to arrive once it connects.</summary>
    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // The user the channel belongs to, and on Windows the logon session, which the mutex is limited to there. Off
    // Windows the user name alone: UserDomainName is the HOST name there, which macOS changes by itself after a name
    // clash on the network, and a later launch then looked for a channel the running instance had not opened.
    private static string UserOf() =>
        OperatingSystem.IsWindows()
            ? $"{Environment.UserDomainName}\\{Environment.UserName}#{System.Diagnostics.Process.GetCurrentProcess().SessionId}"
            : Environment.UserName;

    /// <summary>
    /// Stable key for a scope value. Normalized case- and trailing-separator-insensitive so
    /// <c>C:\App</c>, <c>C:\App\</c> and <c>c:\app</c> collapse to one instance; hashed to hex because a
    /// raw path is not a valid mutex or pipe name.
    /// </summary>
    public static string ChannelKey(string? scope)
    {
        var norm = (scope ?? string.Empty).TrimEnd('\\', '/').ToLowerInvariant();
        uint h = 2166136261; // FNV-1a 32-bit — deterministic, no crypto needed
        foreach (var c in norm)
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    /// <summary>
    /// Acquire the scope with no wait. A mutex failure fails OPEN — an OS hiccup must never block a
    /// legitimate launch.
    /// <para>
    /// ⚠ Abandonment recovery (a predecessor that DIED holding the mutex) is BEST-EFFORT here: some
    /// kernels leave a window between the owning thread ending and the abandoned bit flipping, in which
    /// this reports "already running" against a corpse. Where recovery must be reliable — the
    /// <c>--restarted</c> handoff, or any relaunch overlapping a shutdown — use
    /// <see cref="TryAcquire(TimeSpan)"/>.
    /// </para>
    /// </summary>
    public SingleInstanceResult TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Like <see cref="TryAcquire()"/>, but waits up to <paramref name="waitForPredecessor"/> for a
    /// current owner to let go — the <c>--restarted</c> handoff, where a relaunch overlaps its
    /// predecessor's graceful shutdown (which can legitimately drain for many seconds) while a genuine
    /// double-launch keeps the instant zero-wait answer. The blocking wait also closes the
    /// abandonment-timing race described on <see cref="TryAcquire()"/>.
    /// <para>
    /// ⚠ The contract is CROSS-PROCESS. An OS mutex is per-thread reentrant, so a second guard acquired
    /// on the SAME thread falsely succeeds — in-process tests must hold from a dedicated thread, as a
    /// second process would.
    /// </para>
    /// </summary>
    public SingleInstanceResult TryAcquire(TimeSpan waitForPredecessor)
    {
        // 🔴 IDEMPOTENT: already holding it IS success. An OS mutex is per-thread REENTRANT, so taking a
        // second handle succeeds on the same thread even when this process is the owner — and Dispose
        // could then release only one, leaving the mutex held after shutdown and timing the `--restarted`
        // handoff out against a corpse.
        if (_mutex is not null) return SingleInstanceResult.Acquired;

        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, MutexScope);
            bool owned;
            try
            {
                owned = _mutex.WaitOne(TimeSpan.Zero) ||
                        (waitForPredecessor > TimeSpan.Zero && _mutex.WaitOne(waitForPredecessor));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous instance exited without releasing — the mutex is ours now
            }
            if (!owned)
            {
                _mutex.Dispose();
                _mutex = null;
                return SingleInstanceResult.AlreadyRunning;
            }
            return SingleInstanceResult.Acquired;
        }
        catch (Exception ex)
        {
            // FAIL OPEN, and SAY SO — refusing to start because the OS would not answer is the worse
            // trade for most apps, but it is a different fact from owning the scope.
            _mutex?.Dispose();
            _mutex = null;
            AppCallback.Log(_log, () => $"[Shenora] The single-instance mutex '{MutexName}' could not be taken; this launch runs unguarded",
                LogLevel.Warning, ex);
            return SingleInstanceResult.Unverified;
        }
    }

    /// <summary>
    /// The running instance's side: from now on, each later launch's <see cref="ActivateRunning"/> arrives at
    /// <paramref name="activated"/>, one at a time, on a thread of the channel's own; marshal to the UI thread from
    /// there. Call it once the app can come to the front, after <see cref="TryAcquire()"/> answered
    /// <see cref="SingleInstanceResult.Acquired"/>: a launch waits a few seconds for the channel to open.
    /// </summary>
    /// <param name="activated">Receives each later launch. An exception from it is logged, never thrown.</param>
    /// <exception cref="InvalidOperationException">This guard does not own the scope, or already listens.</exception>
    /// <exception cref="IOException">The channel could not be opened: later launches still exit, and nothing comes
    /// to the front.</exception>
    public void Listen(Action<SingleInstanceLaunch> activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        if (_mutex is null)
            throw new InvalidOperationException("Only the instance that owns the scope listens: TryAcquire first.");
        if (_listening is not null) throw new InvalidOperationException("The guard already listens.");
        var first = NewServer();   // here, so a channel that cannot be opened is the caller's to report
        _listening = new CancellationTokenSource();
        var stop = _listening.Token;
        _listener = Task.Run(() => ListenAsync(first, activated, stop));
    }

    /// <summary>
    /// The later launch's side, after <see cref="SingleInstanceResult.AlreadyRunning"/>: hand the running instance
    /// <paramref name="arguments"/> and this process's working directory, so it comes to the front. False when it
    /// could not be reached within <paramref name="timeout"/> (5 s when null): it has not listened yet, or is
    /// shutting down.
    /// </summary>
    /// <param name="arguments">This launch's arguments, without the executable. Null sends none.</param>
    /// <param name="timeout">How long to wait for the running instance to accept. Null waits 5 s.</param>
    public bool ActivateRunning(IReadOnlyList<string>? arguments = null, TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ChannelName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout ?? TimeSpan.FromSeconds(5));
            client.Write(Encode(new SingleInstanceLaunch(arguments ?? [], Environment.CurrentDirectory)));
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => $"[Shenora] The running instance of {ApplicationName} could not be reached to bring it forward",
                LogLevel.Warning, ex);
            return false;
        }
    }

    /// <summary>
    /// Stop listening and release the scope. Call on the ACQUIRING thread, LAST in shutdown — an explicit release
    /// (not just a closed handle) is what lets a <c>--restarted</c> relaunch waiting in
    /// <see cref="TryAcquire(TimeSpan)"/> proceed immediately instead of via abandonment.
    /// </summary>
    public void Dispose()
    {
        if (_listening is { } listening)
        {
            listening.Cancel();
            try { _listener?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            listening.Dispose();
            _listening = null;
            _listener = null;
        }
        // ReleaseMutex throws when called off the owning thread or when never acquired — both
        // fine to swallow here (the OS still cleans up at process exit).
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    // One instance at a time on Windows, which also makes this the pipe's FIRST instance or fails, so a process that
    // took the name before this one is refused rather than joined. A peer that connected and never let go does not
    // hold it: disposing this side frees the instance (measured on Windows). Two elsewhere — see ListenAsync.
    private NamedPipeServerStream NewServer() =>
        new(ChannelName, PipeDirection.In, OperatingSystem.IsWindows() ? 1 : 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream server, Action<SingleInstanceLaunch> activated, CancellationToken stop)
    {
        while (true)
        {
            try
            {
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                if (await ReadAsync(server, stop).ConfigureAwait(false) is { } launch)
                {
                    AppCallback.Run(() => activated(launch), ex => AppCallback.Log(_log,
                        () => "[Shenora] The app's handling of a later launch threw", LogLevel.Error, ex));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppCallback.Log(_log, () => "[Shenora] A later launch's request could not be read", LogLevel.Warning, ex);
            }
            // 🔴 Off Windows the next instance comes FIRST. There a pipe is a socket its instances share, and it closes
            // with the last of them, dropping every launch queued on it while this one was handled — each of which had
            // connected, written, and reported success. Windows allows one instance, so it can only come after.
            NamedPipeServerStream? next;
            if (OperatingSystem.IsWindows())
            {
                await server.DisposeAsync().ConfigureAwait(false);
                next = await NextServerAsync(stop).ConfigureAwait(false);
            }
            else
            {
                next = await NextServerAsync(stop).ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
            }
            if (next is null) return;
            server = next;
        }
    }

    // The channel's next instance, retried: one that cannot be made now may be made a moment later, and giving up
    // ends activation for the rest of the run. Null once stopped, or when it kept failing (said so).
    private async Task<NamedPipeServerStream?> NextServerAsync(CancellationToken stop)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (stop.IsCancellationRequested) return null;
            try
            {
                return NewServer();
            }
            catch (Exception ex) when (attempt < 10)
            {
                AppCallback.Log(_log, () => "[Shenora] The single-instance channel could not reopen; retrying", LogLevel.Debug, ex);
            }
            catch (Exception ex)
            {
                AppCallback.Log(_log, () => "[Shenora] The single-instance channel closed: a later launch will exit without bringing this one forward",
                    LogLevel.Warning, ex);
                return null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
    }

    // One launch's request, bounded in size and in time, so a connection that never writes cannot hold the channel.
    private async Task<SingleInstanceLaunch?> ReadAsync(Stream stream, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ReadTimeout);
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMessageBytes)
            {
                AppCallback.Log(_log, () => $"[Shenora] A later launch's request passed {MaxMessageBytes} bytes and was dropped", LogLevel.Warning);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Decode(buffer.ToArray());
    }

    internal static byte[] Encode(SingleInstanceLaunch activation)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("arguments");
            foreach (var argument in activation.Arguments) writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WriteString("workingDirectory", activation.WorkingDirectory);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    // Null for anything that is not the shape Encode writes: the channel is the user's own, but still input.
    internal static SingleInstanceLaunch? Decode(byte[] message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("workingDirectory", out var directory) || directory.ValueKind != JsonValueKind.String)
                return null;
            var list = new List<string>(arguments.GetArrayLength());
            foreach (var argument in arguments.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String) return null;
                list.Add(argument.GetString()!);
            }
            return new SingleInstanceLaunch(list, directory.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A shell's single-instance behaviour: <c>WindowsHostOptions.SingleInstance</c> and
/// <c>ChromiumHostOptions.SingleInstance</c>, on by default in both.
/// </summary>
public sealed class SingleInstanceHostOptions
{
    /// <summary>
    /// What "one instance" is scoped to. Null = the app's install root
    /// (<see cref="ShenoraPaths.RootDir"/>), so distinct installs coexist.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Argument a restart-relaunch passes so the gate waits for the outgoing instance instead of
    /// treating the overlap as a double-launch (the restart-through-launcher pattern).
    /// </summary>
    public string RestartArgument { get; init; } = "--restarted";

    /// <summary>
    /// How long a restart-relaunch waits for its predecessor's mutex — a graceful shutdown can spend
    /// many seconds draining before the mutex releases.
    /// </summary>
    public TimeSpan RestartWaitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// What a LOSING launch does before exiting. Null = <see cref="SingleInstanceGuard.ActivateRunning"/> with the
    /// launch's own arguments (the running instance comes to the front). A custom callback replaces that entirely,
    /// and receives the guard so it can still activate.
    /// </summary>
    public Action<ShenoraApplication, SingleInstanceGuard>? OnSecondInstance { get; init; }

    /// <summary>
    /// What the RUNNING instance does with a later launch, on the UI thread, once its main window has been brought
    /// forward: the arguments that launch was given, such as a file to open. Null does nothing more.
    /// <para>
    /// On macOS the Chromium shell brings files and links here too: Finder's "open with", a file dropped on the Dock
    /// icon and a link to a scheme the bundle declares reach an app as Apple Events rather than arguments, the first
    /// launch's own included, and arrive as a launch whose arguments are the paths or the URL. The WinForms shell,
    /// Windows-only, has only launches.
    /// </para>
    /// </summary>
    public Action<ShenoraApplication, SingleInstanceLaunch>? OnActivated { get; init; }
}
