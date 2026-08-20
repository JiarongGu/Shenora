using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Platform.Activities;

using System.Runtime.InteropServices;
using System.Text.Json;
using Shenora;

namespace Shenora.iOS;

/// <summary>
/// iOS's <see cref="ILiveActivities"/> — ActivityKit, reached through the kit's own Swift shim
/// (<c>ShenoraLiveActivity.swift</c>), because ActivityKit has NO Objective-C surface at all: its header is
/// an empty include guard. <c>"__Internal"</c> is the library name because the shim is compiled into a
/// static library and linked into the app binary, so its <c>@_cdecl</c> symbols are in the executable.
/// <para>
/// ⚠ <b>That link is why the kit builds the shim for EVERY iOS app, opted in or not.</b> A
/// <c>DllImport("__Internal")</c> resolves at STATIC LINK time, so gating the shim on the devkit property
/// made the package fail to link — five undefined symbols — for any app that had not enabled the feature.
/// Runtime lookup does not survive the linker either: with nothing referencing the symbols they are not
/// retained. See <c>Shenora.iOS.targets</c>.
/// </para>
/// <para>
/// ⚠ <b>This type does nothing useful unless the app's build includes the widget extension</b> — the
/// activity has no view to render, so <see cref="Start"/> succeeds and nothing appears, and
/// <see cref="Unavailable"/> cannot detect the omission (the OS reports activities as available regardless).
/// </para>
/// </summary>
public sealed class IosLiveActivities : ILiveActivities
{
    private const string Lib = "__Internal";

    /// <summary>The name every activity is started with. One name because v1 renders one KIND of surface; a
    /// view distinguishing several jobs reads the state's title instead.</summary>
    private const string ActivityName = "shenora";

    private readonly ILogger? _log;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a native callback.</param>
    public IosLiveActivities(ILogger? log = null) => _log = log;

    [DllImport(Lib, EntryPoint = "shenora_activity_unavailable")]
    private static extern IntPtr NativeUnavailable();

    [DllImport(Lib, EntryPoint = "shenora_activity_start")]
    private static extern IntPtr NativeStart([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string stateJson,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string appearanceJson,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string layoutJson);

    [DllImport(Lib, EntryPoint = "shenora_activity_update")]
    private static extern IntPtr NativeUpdate([MarshalAs(UnmanagedType.LPUTF8Str)] string handle,
                                              [MarshalAs(UnmanagedType.LPUTF8Str)] string stateJson);

    [DllImport(Lib, EntryPoint = "shenora_activity_end")]
    private static extern IntPtr NativeEnd([MarshalAs(UnmanagedType.LPUTF8Str)] string handle);

    [DllImport(Lib, EntryPoint = "shenora_activity_push_token")]
    private static extern IntPtr NativePushToken([MarshalAs(UnmanagedType.LPUTF8Str)] string handle);

    [DllImport(Lib, EntryPoint = "shenora_activity_free")]
    private static extern void NativeFree(IntPtr p);

    /// <summary>The wire options, defined ONCE in <c>Shenora</c> beside the types they describe. ⚠ The
    /// property names the Swift mirror declares come from this naming policy — read <c>ActivityWire</c>
    /// before changing anything about how these types serialize.</summary>
    private static JsonSerializerOptions Json => ActivityWire.Json;

    /// <summary>Take ownership of a strdup'd Swift string: copy it out, then free it.</summary>
    private static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUTF8(p) ?? string.Empty; }
        finally { NativeFree(p); }
    }

    /// <inheritdoc />
    public string? Unavailable
    {
        get
        {
            try
            {
                var reason = Take(NativeUnavailable());
                return reason.Length == 0 ? null : reason;
            }
            catch (Exception ex)
            {
                // Defensive only: the shim is linked into every iOS app referencing this package, so "not
                // linked" is unreachable. What can still fail is the CALL, and a reason beats a throw.
                Log(() => "[Shenora.iOS] Live activity probe failed.", ex);
                return $"The live-activity shim could not be reached ({ex.GetType().Name}).";
            }
        }
    }

    /// <inheritdoc />
    public string? Start(LiveActivityState state,
                         LiveActivityAppearance? appearance = null,
                         Presentation? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        // ⚠ Both cross as ATTRIBUTES, not state — ActivityKit fixes attributes for the activity's lifetime.
        // What CHANGES is the state, and the tree's `{title}`/`{progress}` bindings are resolved against it
        // at every render, which is how a tree described once keeps showing current values.
        //
        // 🔴 THE SERIALIZATION IS INSIDE THE GUARD. Everything here is APP-SUPPLIED, so `Serialize` is a
        // place an adopter's data can throw — a tree nesting past System.Text.Json's depth limit, or one
        // containing itself — and a progress bar must never be what takes an app down.
        var result = Call(
            () =>
            {
                var look = JsonSerializer.Serialize(appearance ?? new LiveActivityAppearance(), Json);
                // Empty string, not "null": the shim treats empty as "nothing described" without parsing.
                var tree = presentation is null ? "" : JsonSerializer.Serialize(presentation, Json);
                // ⚠ DIAGNOSTIC: the shim reports what it RECEIVED, this reports what was SENT — without the
                // pair, "the app passed none" and "serialisation produced nothing" look identical.
                Log(() => $"[Shenora.iOS] start: presentation={(presentation is null ? "<null>" : $"{tree.Length}B")} "
                          + $"appearance={look.Length}B");
                return Take(NativeStart(ActivityName, JsonSerializer.Serialize(state, Json), look, tree));
            },
            nameof(Start));
        // `!reason` is the shim's failure form; anything else is the handle. Null out the failure so a
        // caller's `is null` check is the whole error contract.
        if (result is null || result.StartsWith('!'))
        {
            Log(() => $"[Shenora.iOS] Live activity refused: {result?[1..] ?? "no response"}");
            return null;
        }
        return result;
    }

    /// <inheritdoc />
    public void Update(string handle, LiveActivityState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentNullException.ThrowIfNull(state);
        var result = Call(() => Take(NativeUpdate(handle, JsonSerializer.Serialize(state, Json))),
            nameof(Update));
        if (result is { Length: > 0 } && result.StartsWith('!'))
            Log(() => $"[Shenora.iOS] Live activity update refused: {result[1..]}");
    }

    /// <inheritdoc />
    public void End(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return;
        Call(() => Take(NativeEnd(handle)), nameof(End));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Empty from the shim means "not issued yet", the NORMAL answer immediately after <see cref="Start"/>
    /// — the system delivers the token asynchronously. Mapped to null so a caller cannot register "" with
    /// their server as if it were an address.
    /// </remarks>
    public string? PushToken(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        var token = Call(() => Take(NativePushToken(handle)), nameof(PushToken));
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>Every native call goes through here: a missing symbol, a torn-down activity or a malformed
    /// payload must not become an app crash.</summary>
    private string? Call(Func<string> call, string what)
    {
        try { return call(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.iOS] LiveActivities.{what} failed.", ex);
            return null;
        }
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);
}
