using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Windows;

/// <summary>Inputs for one <see cref="SecondaryWindows.Open"/> call.</summary>
public sealed class SecondaryWindowOptions
{
    /// <summary>
    /// Creates the window — runs ON the window's own STA thread, so every control it creates gets that
    /// thread's message pump. Create it, don't show it: the pump shows it after geometry is applied.
    /// </summary>
    public required Func<Form> CreateForm { get; init; }

    /// <summary>Geometry persistence for this named window — the same window-state stack the main window
    /// uses. One store per name (e.g. <c>JsonFileWindowStateStore("windows/{name}.json")</c>); null =
    /// none.</summary>
    public IWindowStateStore? StateStore { get; init; }

    /// <summary>Sizing defaults/minimums for <see cref="StateStore"/>. Null = defaults.</summary>
    public WindowStateOptions? StateOptions { get; init; }
}

/// <summary>
/// Named secondary windows, each on its OWN STA thread with its own message pump. One window per name;
/// <see cref="Open"/> on an existing name ACTIVATES it rather than recreating it.
/// <para>
/// ⚠ Everything marshals with non-blocking <c>BeginInvoke</c> — a blocking <c>Invoke</c> from the IPC
/// thread deadlocks the UI. Window threads are BACKGROUND, so an app exit never hangs on a forgotten
/// window; dispose (or <see cref="CloseAll"/>) closes them gracefully first so geometry saves run.
/// </para>
/// </summary>
public sealed class SecondaryWindows : IDisposable
{
    private sealed class WindowEntry
    {
        public volatile Form? Form;
        public volatile bool CloseRequested;

        // ⚠ Pre-handle intent, same mechanism as CloseRequested: the marshal is a no-op before the handle
        // exists, so an Activate arriving while the window thread starts up would be silently lost.
        public volatile bool ActivateRequested;
    }

    private readonly ILogger<SecondaryWindows> _logger;
    private readonly ConcurrentDictionary<string, WindowEntry> _windows = new();
    private bool _disposed;

    /// <summary>The registry of windows running their own STA message pumps. Logger optional.</summary>
    public SecondaryWindows(ILogger<SecondaryWindows>? logger = null)
    {
        _logger = logger ?? NullLogger<SecondaryWindows>.Instance;
    }

    /// <summary>Open the named window on its own STA thread. Returns false (and activates the existing
    /// window) when the name is already open.</summary>
    public bool Open(string name, SecondaryWindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);

        var entry = new WindowEntry();
        if (!_windows.TryAdd(name, entry))
        {
            Activate(name);
            return false;
        }

        var thread = new Thread(() => RunWindow(name, entry, options))
        {
            Name = $"SecondaryWindow:{name}",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // ⚠ Without this a failed Start leaves the entry behind forever — RunWindow, the only other
            // cleanup path, never ran — so the name stays permanently "already open".
            _windows.TryRemove(name, out _);
            _logger.LogError(ex, "Secondary window '{Name}' could not start its thread", name);
            throw;
        }
        return true;
    }

    private void RunWindow(string name, WindowEntry entry, SecondaryWindowOptions options)
    {
        Form form;
        try
        {
            form = options.CreateForm();

            if (options.StateStore is { } store)
            {
                // AttachTo owns the apply-before-show / save-on-closed ordering.
                new WindowStateManager(store, options.StateOptions).AttachTo(form);
            }

            // 🔴 NO FormClosed removal here: Application.Run has NOT returned when FormClosed fires, so
            // Dispose() — which waits on _windows becoming empty — would see "empty" mid-teardown and let
            // the process exit while a WebView2 child was still shutting down, leaving its user-data
            // folder LOCKED. The `finally` after Application.Run is the only correct removal point.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secondary window '{Name}' failed to create", name);
            _windows.TryRemove(name, out _);
            return;
        }

        entry.Form = form;
        // A Close(name) that raced window creation lands here instead of being lost…
        if (entry.CloseRequested)
        {
            form.Dispose();
            _windows.TryRemove(name, out _);
            return;
        }
        // …and one that lands between this check and the pump creating the handle lands here
        // (Post is a deliberate no-op pre-handle — see below).
        form.HandleCreated += (_, _) =>
        {
            if (entry.CloseRequested) form.BeginInvoke(form.Close);
            // An Activate that arrived before the handle existed was dropped by the marshal; replay it
            // now. Close wins if both are pending.
            else if (entry.ActivateRequested)
            {
                entry.ActivateRequested = false;
                form.BeginInvoke(() => WindowActivation.BringToFront(form));
            }
        };

        _logger.LogDebug("Secondary window '{Name}' opened on thread {Thread}", name, Environment.CurrentManagedThreadId);
        try
        {
            Application.Run(form); // this thread's own pump — returns when the form closes
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secondary window '{Name}' pump faulted", name);
        }
        finally
        {
            _windows.TryRemove(name, out _);
            _logger.LogDebug("Secondary window '{Name}' closed", name);
        }
    }

    /// <summary>True while the named window is open (or opening).</summary>
    public bool HasWindow(string name) => _windows.ContainsKey(name);

    /// <summary>Bring the named window to the front (no-op when it isn't open). Survives being called
    /// while the window is still opening: recorded and replayed once the handle exists.</summary>
    public void Activate(string name)
    {
        if (!_windows.TryGetValue(name, out var entry)) return;

        // Set the flag FIRST, unconditionally: the form may not exist yet, and even when it does the Post
        // below is a no-op until the handle is created. Cleared on the success path so it cannot refire.
        entry.ActivateRequested = true;
        if (entry.Form is not { } form) return;
        if (Post(form, () => WindowActivation.BringToFront(form)))
            entry.ActivateRequested = false;
    }

    /// <summary>Close the named window (non-blocking; safe from any thread).</summary>
    public void Close(string name)
    {
        if (!_windows.TryGetValue(name, out var entry)) return;
        entry.CloseRequested = true;
        if (entry.Form is { } form) Post(form, form.Close);
    }

    /// <summary>Close every window.</summary>
    public void CloseAll()
    {
        foreach (var name in _windows.Keys.ToArray()) Close(name);
    }

    /// <summary>Close every window and WAIT (bounded) for their pumps to finish — the window threads are
    /// background, so an unwaited dispose at app exit kills them before their FormClosed-driven geometry
    /// saves run. Bounded, so a wedged window can never hang shutdown.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAll();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!_windows.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
    }

    internal int WindowCount => _windows.Count;

    internal Form? TryGetForm(string name) =>
        _windows.TryGetValue(name, out var entry) ? entry.Form : null;

    // Non-blocking marshal to the window's own thread. ⚠ Pre-handle this is a deliberate NO-OP: the
    // caller is never the window's own thread, so running inline would CREATE the handle on the wrong
    // thread and kill the pump.
    private static bool Post(Form form, Action action) => new WinFormsUiDispatcher(form).Post(action);
}
