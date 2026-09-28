// The struct-layout check `node devtools/dev.mjs cef-binding` runs for each OS, compiled beside THAT OS's generated
// binding (Common + one OS folder) in a throwaway project of its own, so it shares nothing with the real build.
//
//   args: <layout.json> <os>     layout.json = { "<struct>": <size in bytes on x86_64, from clang> }
//
// The sizes are clang's own layout of CEF's C structs for the OS's x86_64 target (ClangSharp's generated tests).
// A C# struct that disagrees reads CEF's memory wrongly on that OS, and nothing else would say so until it crashed
// there. Every per-OS difference is pointer- or C-type-sized, which x86_64 lays out the same on each OS, so all three
// are checked on this machine.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

var table = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(args[0]))!;
var os = args[1];
var sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
var binding = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.Namespace == "Shenora.Chromium.Interop" && t.IsValueType && !t.IsEnum && t.Name.StartsWith("_cef_", StringComparison.Ordinal))
    .ToDictionary(t => t.Name);

var bad = new List<string>();
foreach (var (name, size) in table)
{
    if (!binding.TryGetValue(name, out var type)) { bad.Add($"{name}: in clang's layout, not in the binding"); continue; }
    var actual = (int)sizeOf.MakeGenericMethod(type).Invoke(null, null)!;
    if (actual != size) bad.Add($"{name}: {actual} bytes in C#, {size} in C");
    if (!type.IsLayoutSequential) bad.Add($"{name}: not sequential");
    if (Marshal.SizeOf(type) != actual) bad.Add($"{name}: not blittable ({Marshal.SizeOf(type)} marshalled)");
}
foreach (var name in binding.Keys.Except(table.Keys)) bad.Add($"{name}: in the binding, with no layout from clang");

if (table.Count < 100) bad.Add($"only {table.Count} layouts read: the parser matched too little");
Console.WriteLine(bad.Count == 0 ? $"cef-layout {os}: {table.Count} structs match clang's layout" : $"cef-layout {os}: {bad.Count} problem(s)\n  " + string.Join("\n  ", bad));
return bad.Count == 0 ? 0 : 1;
