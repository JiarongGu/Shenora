namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>A control byte in a source file is an escape that went wrong on the way in.</b> A path written <c>@"C:\in\b.png"</c>
/// through a scripted edit lands as <c>C:in</c>, a backspace, <c>.png</c>: it compiles, the test that compares it to itself
/// passes, and the literal is no longer what it says. Tab, carriage return and line feed are the only control bytes source
/// holds.
/// </summary>
public class ControlCharacterTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(ControlCharacterTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    private static IEnumerable<string> SourceFiles()
    {
        foreach (var folder in new[] { "src", "tests", "samples" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("node_modules/", StringComparison.Ordinal)) continue;
                yield return file;
            }
    }

    /// <summary>
    /// The control bytes in one file, as <c>line:0xNN</c>.
    /// </summary>
    internal static IEnumerable<string> ControlBytes(byte[] content)
    {
        var line = 1;
        foreach (var b in content)
        {
            if (b == '\n') line++;
            else if (b < 0x20 && b != '\t' && b != '\r') yield return $"{line}:0x{b:X2}";
        }
    }

    [Fact]
    public void No_source_file_holds_a_control_byte()
    {
        var found = SourceFiles()
            .SelectMany(file => ControlBytes(File.ReadAllBytes(file)).Select(at => $"{Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/')}:{at}"))
            .ToList();

        Assert.True(found.Count == 0, "Control bytes in source (line:byte):\n  " + string.Join("\n  ", found)
            + "\n\nA backslash escape was read by something between the edit and the file: write the literal again with the edit tool.");
    }

    [Fact]
    public void The_scan_sees_a_control_byte_and_leaves_tabs_and_line_ends()
    {
        Assert.Equal(["2:0x08"], ControlBytes("a\r\nC:in\b.png\n\tb\n"u8.ToArray()));
        Assert.True(SourceFiles().Count() > 300, "the scan found almost nothing");
    }
}
