using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Windows binding against clang's own layout of CEF's C structs (<see cref="CefStructLayout"/>, written by
/// <c>node devtools/dev.mjs cef-binding</c>, which checks the macOS and Linux bindings the same way as it writes them).
/// A C# struct whose size disagrees reads CEF's memory wrongly, and nothing else says so until it crashes.
/// </summary>
public class CefStructLayoutTests
{
    [Fact]
    public void Every_struct_of_the_Windows_binding_has_the_size_clang_gives_it()
    {
        var sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
        var bad = new List<string>();
        foreach (var (type, size) in CefStructLayout.Windows)
        {
            var actual = (int)sizeOf.MakeGenericMethod(type).Invoke(null, null)!;
            if (actual != size) bad.Add($"{type.Name}: {actual} bytes in C#, {size} in C");
            if (!type.IsLayoutSequential) bad.Add($"{type.Name}: not sequential");
            if (Marshal.SizeOf(type) != actual) bad.Add($"{type.Name}: not blittable");
        }

        Assert.True(CefStructLayout.Windows.Length > 100, $"only {CefStructLayout.Windows.Length} layouts: the table was not written whole");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Every_struct_of_the_binding_has_a_layout_to_be_checked_against()
    {
        var checkedTypes = CefStructLayout.Windows.Select(row => row.Type).ToHashSet();
        var unchecked_ = typeof(Shenora.Chromium.ChromiumEngine).Assembly.GetTypes()
            .Where(t => t.Namespace == "Shenora.Chromium.Interop" && t.IsValueType && !t.IsEnum && t.Name.StartsWith("_cef_", StringComparison.Ordinal))
            .Where(t => !checkedTypes.Contains(t))
            .Select(t => t.Name)
            .ToList();

        // A struct added by hand, or a binding regenerated without its table, would otherwise go unchecked.
        Assert.True(unchecked_.Count == 0, "no layout for: " + string.Join(", ", unchecked_));
    }
}
