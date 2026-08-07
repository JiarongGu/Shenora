#if IOS || MACCATALYST
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shenora;

namespace Shenora.Mobile;

/// <summary>
/// iOS's <see cref="ILiveActivities"/> — ActivityKit, reached through the kit's own Swift shim.
/// <para>
/// The shim exists because ActivityKit has NO Objective-C surface at all: its header is an empty include
/// guard, verified against the SDK. So even <c>start</c>/<c>update</c>/<c>end</c> has to cross through Swift,
/// and the kit ships that Swift (<c>ShenoraLiveActivity.swift</c>) rather than making every app write it.
/// </para>
/// <para>
/// <c>"__Internal"</c> is the library name because the shim is compiled into a static library and linked into
/// the app binary, so its <c>@_cdecl</c> symbols are in the executable itself — measured with <c>nm</c>, not
/// assumed.
/// </para>
/// <para>
/// ⚠ <b>That link is why the kit builds the shim for EVERY iOS app, opted in or not.</b> A
/// <c>DllImport("__Internal")</c> is resolved at STATIC LINK time, so gating the shim on the devkit property
/// made the package fail to link — five undefined symbols — for any app that had not enabled the feature.
/// Shipped that way in 0.9.0 and reported by the first adopter. Runtime lookup was tried instead and did not
/// survive the linker: with nothing referencing the symbols they were not retained, measured as present in
/// the archive and absent from the app binary. See <c>Shenora.iOS.targets</c>.
/// </para>
/// <para>
/// ⚠ <b>This type does nothing useful unless the app's build includes the widget extension</b>, because the
/// activity has no view to render otherwise: <see cref="Start"/> succeeds and nothing appears.
/// <c>Shenora.iOS.targets</c> is what builds it, and <see cref="Unavailable"/> cannot detect the omission —
/// the OS reports activities as available regardless.
/// </para>
/// </summary>
public sealed class MobileLiveActivities : ILiveActivities
{
    private const string Lib = "__Internal";

    /// <summary>
    /// The name every activity is started with. One name because v1 renders one KIND of surface; a view that
    /// wants to distinguish several jobs reads the state's title instead.
    /// </summary>
    private const string ActivityName = "shenora";

    private readonly Action<string>? _log;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a native callback.</param>
    public MobileLiveActivities(Action<string>? log = null) => _log = log;

    [DllImport(Lib, EntryPoint = "shenora_activity_unavailable")]
    private static extern IntPtr NativeUnavailable();

    [DllImport(Lib, EntryPoint = "shenora_activity_start")]
    private static extern IntPtr NativeStart([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string stateJson);

    [DllImport(Lib, EntryPoint = "shenora_activity_update")]
    private static extern IntPtr NativeUpdate([MarshalAs(UnmanagedType.LPUTF8Str)] string handle,
                                              [MarshalAs(UnmanagedType.LPUTF8Str)] string stateJson);

    [DllImport(Lib, EntryPoint = "shenora_activity_end")]
    private static extern IntPtr NativeEnd([MarshalAs(UnmanagedType.LPUTF8Str)] string handle);

    [DllImport(Lib, EntryPoint = "shenora_activity_free")]
    private static extern void NativeFree(IntPtr p);

    /// <summary>
    /// camelCase and OMIT NULLS. Both matter: the Swift mirror declares camelCase properties, and a null
    /// written explicitly decodes to nil anyway but makes the payload bigger for no gain. Nulls are the
    /// common case here — most states set one or two fields.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
                // Defensive only. The shim is now linked into EVERY iOS app that references this package, so
                // "not linked" is no longer a reachable state — and an earlier version of this property
                // promised to report exactly that, which a LINK-time failure could never deliver. What can
                // still fail is the call itself, and a reason is worth returning rather than throwing at an
                // app that asked a simple question.
                Log(() => $"[Shenora.Mobile] Live activity probe failed ({ex.GetType().Name}: {ex.Message}).");
                return $"The live-activity shim could not be reached ({ex.GetType().Name}).";
            }
        }
    }

    /// <inheritdoc />
    public string? Start(LiveActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var result = Call(() => Take(NativeStart(ActivityName, JsonSerializer.Serialize(state, Json))),
            nameof(Start));
        // `!reason` is the shim's failure form; anything else is the handle. Null out the failure so a
        // caller's `is null` check is the whole error contract.
        if (result is null || result.StartsWith('!'))
        {
            Log(() => $"[Shenora.Mobile] Live activity refused: {result?[1..] ?? "no response"}");
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
            Log(() => $"[Shenora.Mobile] Live activity update refused: {result[1..]}");
    }

    /// <inheritdoc />
    public void End(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return;
        Call(() => Take(NativeEnd(handle)), nameof(End));
    }

    /// <summary>
    /// Every native call goes through here. A missing symbol, a torn-down activity or a malformed payload
    /// must not become an app crash — an app reporting progress on a background job is the last place a
    /// throw is wanted.
    /// </summary>
    private string? Call(Func<string> call, string what)
    {
        try { return call(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Mobile] LiveActivities.{what} failed ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);
}
#endif
