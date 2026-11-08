namespace Shenora.Tests.Chromium;

/// <summary>
/// Reads, and for a fixture writes, a PE file's extended DLL characteristics: the 4-byte payload of its debug directory
/// entry of type 20 (<c>IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS</c>), where CEF's launcher declares itself CET-compatible (0x1)
/// and where the layout adds the relaxed context-IP mode (0x4).
/// </summary>
internal static class LauncherPe
{
    public const uint CetCompatible = 0x1;
    public const uint RelaxedContextIp = 0x4;
    private const uint ExDllCharacteristics = 20;

    /// <summary>The flags, or null for a file whose debug directory has no such entry.</summary>
    public static uint? Flags(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Find(bytes) is { } data ? BitConverter.ToUInt32(bytes, data) : null;
    }

    /// <summary>A copy of a real executable that declares itself CET-compatible, as CEF's launcher does: its first debug
    /// entry, CodeView, becomes the extended-characteristics entry carrying <see cref="CetCompatible"/>.</summary>
    public static void MakeCetCompatible(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (entries, count) = DebugEntries(bytes);
        Assert.True(count > 0, "the fixture executable has no debug directory to repurpose");
        BitConverter.GetBytes(ExDllCharacteristics).CopyTo(bytes, entries + 12);
        BitConverter.GetBytes(4u).CopyTo(bytes, entries + 16);
        BitConverter.GetBytes(CetCompatible).CopyTo(bytes, (int)BitConverter.ToUInt32(bytes, entries + 24));
        File.WriteAllBytes(path, bytes);
    }

    private static int? Find(byte[] bytes)
    {
        var (entries, count) = DebugEntries(bytes);
        for (var i = 0; i < count; i++)
        {
            var entry = entries + i * 28;
            if (BitConverter.ToUInt32(bytes, entry + 12) == ExDllCharacteristics && BitConverter.ToUInt32(bytes, entry + 16) >= 4)
                return (int)BitConverter.ToUInt32(bytes, entry + 24);
        }
        return null;
    }

    // The file offset of the debug directory, and how many entries it holds.
    private static (int Offset, int Count) DebugEntries(byte[] bytes)
    {
        var pe = BitConverter.ToInt32(bytes, 0x3c);
        var sections = BitConverter.ToUInt16(bytes, pe + 6);
        var optional = pe + 24;
        var directory = optional + (BitConverter.ToUInt16(bytes, optional) == 0x20b ? 112 : 96) + 6 * 8;
        var rva = BitConverter.ToUInt32(bytes, directory);
        var size = BitConverter.ToUInt32(bytes, directory + 4);
        if (rva == 0) return (0, 0);
        var table = optional + BitConverter.ToUInt16(bytes, pe + 20);
        for (var i = 0; i < sections; i++)
        {
            var section = table + i * 40;
            var virtualAddress = BitConverter.ToUInt32(bytes, section + 12);
            var span = Math.Max(BitConverter.ToUInt32(bytes, section + 8), BitConverter.ToUInt32(bytes, section + 16));
            if (rva >= virtualAddress && rva < virtualAddress + span)
                return ((int)(rva - virtualAddress + BitConverter.ToUInt32(bytes, section + 20)), (int)(size / 28));
        }
        return (0, 0);
    }
}
