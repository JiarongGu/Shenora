using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Shenora.Tests.Api;

/// <summary>
/// Reads an assembly's public surface straight out of the IL metadata tables — no loading, no type
/// resolution, no resolver to maintain.
/// <para>
/// It exists for exactly one problem: <c>Shenora.Mobile</c> targets <c>net10.0-android</c>, so this
/// <c>net10.0-windows</c> test project cannot reference it, and neither of the normal routes works.
/// <c>Assembly.LoadFrom</c> would have to resolve <c>Microsoft.Maui.Controls</c> and the Android
/// facades to render a member like <c>MobileUiDispatcher(IDispatcher)</c>; a <c>MetadataLoadContext</c>
/// avoids that but cannot drive <see cref="NullabilityInfoContext"/>, which
/// <see cref="ApiSurfaceDump"/> uses in four places. A <see cref="MetadataReader"/> needs neither: it
/// reads the tables as data.
/// </para>
/// <para>
/// <b>The trade, stated because a SemVer gate must not be quietly weaker than it looks:</b> this is a
/// NAME-level surface — types and members. It catches an add, a removal and a rename. It does NOT
/// catch a signature-only change (<c>string?</c> → <c>string</c>, a dropped default value,
/// <c>set</c> → <c>init</c>), all of which the five full baselines do catch. That is why the package
/// is kept thin, and why this file says so rather than letting the baseline imply parity.
/// </para>
/// </summary>
internal static class MetadataSurface
{
    /// <summary>Render the public/protected surface as deterministic, sorted, reviewable text.</summary>
    public static string Render(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        // Group BEFORE sorting. Sorting a flat list of type lines and indented member lines together
        // puts every member (leading spaces) ahead of every type, which renders eight orphaned
        // `.ctor` lines with nothing to attach them to — the first cut did exactly that, and a
        // baseline nobody can read as a diff is not a gate.
        var types = new List<(string Name, IReadOnlyList<string> Members)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!IsVisible(reader, type)) continue;
            types.Add((FullName(reader, type), Members(reader, type)));
        }

        var sb = new StringBuilder();
        // Sorted, not source order: the file is reviewed as a diff, and a member moving within a
        // class must not read as a change.
        foreach (var (name, members) in types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(name);
            foreach (var member in members) sb.Append("  ").AppendLine(member);
        }
        return sb.ToString();
    }

    /// <summary>Exported type SIMPLE names — the input to the genericity (vocabulary) gate.</summary>
    public static IEnumerable<string> ExportedTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!IsVisible(reader, type)) continue;
            yield return reader.GetString(type.Name);
        }
    }

    /// <summary>
    /// Public, or nested inside a visible type as public/protected. Compiler-generated types are
    /// skipped by name (<c>&lt;&gt;c</c>, iterator and async state machines) — they are emitted
    /// PUBLIC in some shapes and are not surface.
    /// </summary>
    private static bool IsVisible(MetadataReader reader, TypeDefinition type)
    {
        if (reader.GetString(type.Name).Contains('<', StringComparison.Ordinal)) return false;

        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public) return true;
        if (visibility is not (TypeAttributes.NestedPublic or TypeAttributes.NestedFamily
            or TypeAttributes.NestedFamORAssem)) return false;

        var declaring = type.GetDeclaringType();
        return !declaring.IsNil && IsVisible(reader, reader.GetTypeDefinition(declaring));
    }

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static IReadOnlyList<string> Members(MetadataReader reader, TypeDefinition type)
    {
        // Accessors are rendered as part of their property/event, exactly as ApiSurfaceDump does —
        // get_X/set_X lines are noise and hide the member they belong to.
        var accessors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            var pa = property.GetAccessors();
            AddName(reader, accessors, pa.Getter);
            AddName(reader, accessors, pa.Setter);
        }
        foreach (var handle in type.GetEvents())
        {
            var @event = reader.GetEventDefinition(handle);
            var ea = @event.GetAccessors();
            AddName(reader, accessors, ea.Adder);
            AddName(reader, accessors, ea.Remover);
            AddName(reader, accessors, ea.Raiser);
        }

        var members = new List<string>();

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);
            if (accessors.Contains(name) || name.Contains('<', StringComparison.Ordinal)) continue;
            if (!IsVisibleMember(method.Attributes & MethodAttributes.MemberAccessMask)) continue;
            members.Add($"method {name}");
        }

        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            var name = reader.GetString(field.Name);
            if (name.Contains('<', StringComparison.Ordinal)) continue;
            var access = field.Attributes & FieldAttributes.FieldAccessMask;
            if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)) continue;
            members.Add($"field {name}");
        }

        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            var pa = property.GetAccessors();
            if (!IsVisibleAccessor(reader, pa.Getter) && !IsVisibleAccessor(reader, pa.Setter)) continue;
            members.Add($"property {reader.GetString(property.Name)}");
        }

        foreach (var handle in type.GetEvents())
        {
            var @event = reader.GetEventDefinition(handle);
            var ea = @event.GetAccessors();
            if (!IsVisibleAccessor(reader, ea.Adder) && !IsVisibleAccessor(reader, ea.Remover)) continue;
            members.Add($"event {reader.GetString(@event.Name)}");
        }

        members.Sort(StringComparer.Ordinal);
        return members;
    }

    private static void AddName(MetadataReader reader, HashSet<string> into, MethodDefinitionHandle handle)
    {
        if (!handle.IsNil) into.Add(reader.GetString(reader.GetMethodDefinition(handle).Name));
    }

    private static bool IsVisibleAccessor(MetadataReader reader, MethodDefinitionHandle handle) =>
        !handle.IsNil && IsVisibleMember(
            reader.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask);

    private static bool IsVisibleMember(MethodAttributes access) =>
        access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
}
