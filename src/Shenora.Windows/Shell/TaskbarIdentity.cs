using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// A window's own taskbar identity: the AppUserModelID the taskbar groups its button under, and the command a pin made from
/// the running window starts, written through the window's property store (<c>SHGetPropertyStoreForWindow</c>).
/// <para>
/// 🔴 <b>Per window, not per process.</b> The relaunch properties are WINDOW properties, read only beside a window-level id,
/// so the id is written there too. <c>SetCurrentProcessExplicitAppUserModelID</c> would also relabel everything else the
/// process shows (the tray icon's hidden window, its toasts): a change no window-level test could see, so it is not used.
/// </para>
/// <para>
/// 🔴 <b>From each handle, back at its end.</b> A property belongs to one window handle, and a recreated handle carries
/// nothing of the old one, so <see cref="Wear"/> writes the identity as each handle is created. Windows asks a window's
/// properties to be removed before it is closed, or what they hold is not given back, so each is emptied as the handle is
/// destroyed. The relaunch properties are set before the id, the order the documentation gives for the pinning one.
/// </para>
/// <para>A window that cannot be labelled still opens: the cost is a second taskbar button, as before.</para>
/// </summary>
public sealed class TaskbarIdentity
{
    /// <summary>The id the taskbar groups under: at most 128 characters, no spaces. Windows matches it against a pin's.</summary>
    public required string AppId { get; init; }

    /// <summary>
    /// The command line a pin made from the running window starts, given by the app (<see cref="Environment.ProcessPath"/> is
    /// what most want). Needs <see cref="RelaunchDisplayName"/>: Windows ignores one without it. Null: a pin starts the
    /// executable, as it would without an identity.
    /// </summary>
    public string? RelaunchCommand { get; init; }

    /// <summary>The name a pin shows. Required when <see cref="RelaunchCommand"/> is set.</summary>
    public string? RelaunchDisplayName { get; init; }

    /// <summary>The icon a pin shows, as a resource string: <c>path,index</c> (a negative index is a resource id). Null: the executable's.</summary>
    public string? RelaunchIcon { get; init; }

    /// <summary>
    /// Have <paramref name="form"/> wear this identity for as long as it has a handle, from now if it has one. On the form's
    /// own thread.
    /// </summary>
    /// <exception cref="ArgumentException">The identity is one Windows would ignore: see <see cref="AppId"/> and <see cref="RelaunchCommand"/>.</exception>
    public void Wear(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Validate();

        form.HandleCreated += (_, _) => Write(form.Handle, Properties());
        form.HandleDestroyed += (_, _) =>
        {
            if (form.IsHandleCreated) Write(form.Handle, Cleared);
        };
        if (form.IsHandleCreated) Write(form.Handle, Properties());
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(AppId) || AppId.Length > 128 || AppId.Contains(' '))
            throw new ArgumentException("An AppId is 1 to 128 characters with no spaces.", nameof(AppId));
        if (RelaunchCommand is { Length: > 0 } && string.IsNullOrEmpty(RelaunchDisplayName))
            throw new ArgumentException("A RelaunchCommand needs a RelaunchDisplayName: Windows ignores one without.", nameof(RelaunchDisplayName));
    }

    // PKEY_AppUserModel_ID, RelaunchCommand, RelaunchIconResource, RelaunchDisplayNameResource: one format id, four property ids.
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    internal const uint RelaunchCommandKey = 2, RelaunchIconKey = 3, RelaunchDisplayNameKey = 4, IdKey = 5;

    private (uint Key, string? Value)[] Properties() =>
    [
        (RelaunchCommandKey, RelaunchCommand),
        (RelaunchDisplayNameKey, RelaunchDisplayName),
        (RelaunchIconKey, RelaunchIcon),
        (IdKey, AppId),
    ];

    private static readonly (uint Key, string? Value)[] Cleared =
        [(IdKey, null), (RelaunchCommandKey, null), (RelaunchDisplayNameKey, null), (RelaunchIconKey, null)];

    private const ushort VtLpwstr = 31;

    /// <summary>Each value as a string, or empty (<c>VT_EMPTY</c>) to remove it. Stored at once: no commit.</summary>
    internal static void Write(IntPtr window, (uint Key, string? Value)[] properties)
    {
        if (window == IntPtr.Zero) return;

        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store is null) return;

            foreach (var (key, value) in properties)
            {
                var name = new PropertyKey { FormatId = AppUserModel, PropertyId = key };
                var variant = new PropVariant();
                try
                {
                    if (value is not null)
                    {
                        variant.Type = VtLpwstr;
                        variant.Pointer = Marshal.StringToCoTaskMemUni(value);
                    }
                    _ = store.SetValue(ref name, ref variant);
                }
                finally
                {
                    // The store copies what it is given, so the string is ours to free.
                    if (variant.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(variant.Pointer);
                }
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            // Not worth a window: an unlabelled one is grouped by its executable.
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The string the window's property store holds under <paramref name="key"/>, or null when it holds none.</summary>
    internal static string? Read(IntPtr window, uint key)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store is null) return null;
            var name = new PropertyKey { FormatId = AppUserModel, PropertyId = key };
            if (store.GetValue(ref name, out var variant) < 0) return null;
            try
            {
                return variant.Type == VtLpwstr && variant.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(variant.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>A <c>PROPVARIANT</c> carrying a string or nothing: its type, three reserved words, and the union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Rest;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);
}
