using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Shenora.Tests.Api;

/// <summary>
/// Renders an assembly's CONSUMER-VISIBLE surface as deterministic, reviewable text — the input to the
/// SemVer approval gate in <see cref="ApiSurfaceTests"/>.
/// <para>
/// This replaced a one-line <c>type.GetMembers(BindingFlags.Public …)</c> dump that was blind to most of
/// what actually breaks a consumer (P5.5 H6). Everything below is here because its absence hid a real
/// break shape:
/// </para>
/// <list type="bullet">
/// <item><b>protected members.</b> <c>BaseFacade.RouteMessageAsync</c> — the one member EVERY consumer
/// overrides — was entirely outside the gate.</item>
/// <item><b>Default parameter values.</b> <c>new EventBus()</c> compiles because the logger parameter is
/// optional; the old dump rendered the constructor as if it were not, so DROPPING a <c>= null</c> — a
/// source break for every caller — showed no diff at all. Eight constructors have that shape.</item>
/// <item><b><c>init</c> vs <c>set</c>, and <c>required</c>.</b> Turning <c>set</c> into <c>init</c>
/// breaks every post-construction assignment; adding <c>required</c> breaks every existing object
/// initializer. Both were invisible.</item>
/// <item><b><c>static</c> vs instance</b>, <b>parameter names</b> (named arguments are a source
/// contract), <b>generic constraints</b>, and <b>nullability</b> — a reference parameter going from
/// <c>string?</c> to <c>string</c> is a break the old dump could not see.</item>
/// <item><b>Attributes.</b> <c>[JsonPropertyName]</c> IS the wire contract: renaming one silently breaks
/// the C#⇄TS mirror while every test still passes.</item>
/// </list>
/// <para>
/// Property and event accessors are rendered as part of their member (<c>{ get; init; }</c>) rather than
/// as separate <c>get_X()</c>/<c>set_X()</c> lines, which is both shorter and strictly more informative.
/// </para>
/// </summary>
internal static class ApiSurfaceDump
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Attribute namespaces that describe the COMPILER's encoding, not the API's contract.</summary>
    private static readonly string[] NoiseAttributeNamespaces =
    [
        "System.Runtime.CompilerServices",
        "System.Diagnostics.CodeAnalysis",
    ];

    /// <summary>
    /// Debugging aids, not contract. Filtered individually rather than by namespace, because
    /// <c>System.Diagnostics</c> also holds <c>[Conditional]</c>, which genuinely changes whether call
    /// sites are emitted and must never be hidden.
    /// </summary>
    private static readonly string[] NoiseAttributeNames =
    [
        "System.Diagnostics.DebuggerStepThroughAttribute",
        "System.Diagnostics.DebuggerHiddenAttribute",
        "System.Diagnostics.DebuggerDisplayAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute",
        "System.Diagnostics.DebuggerNonUserCodeAttribute",
    ];

    /// <summary>
    /// C# aliases for the primitives. Purely for READABILITY, which matters: this file is reviewed by a
    /// human on every intentional surface change, and `System.Void`/`System.Boolean` make a diff harder
    /// to read than it needs to be. The mapping is fixed, so it cannot introduce churn.
    /// </summary>
    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char", [typeof(decimal)] = "decimal", [typeof(double)] = "double",
        [typeof(float)] = "float", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
        [typeof(ulong)] = "ulong", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(object)] = "object", [typeof(string)] = "string",
    };

    internal static string Render(Assembly assembly)
    {
        var sb = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.AppendLine(RenderTypeHeader(type));
            foreach (var line in RenderMembers(type).OrderBy(s => s, StringComparer.Ordinal))
                sb.AppendLine("  " + line);
        }
        return sb.ToString();
    }

    // ── types ─────────────────────────────────────────────────────────────────────────────────────

    private static string RenderTypeHeader(Type type)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(type)) sb.Append(attribute).Append(' ');

        if (type.IsEnum) sb.Append("enum ");
        else if (type.IsInterface) sb.Append("interface ");
        else if (typeof(Delegate).IsAssignableFrom(type)) sb.Append("delegate ");
        else if (type.IsValueType) sb.Append(type.IsByRefLike ? "ref struct " : "struct ");
        else
        {
            // static == abstract + sealed; say so explicitly, because turning a static class into an
            // instantiable one (or back) changes how every consumer references it.
            if (type is { IsAbstract: true, IsSealed: true }) sb.Append("static ");
            else if (type.IsAbstract) sb.Append("abstract ");
            else if (type.IsSealed) sb.Append("sealed ");
            sb.Append("class ");
        }

        sb.Append(TypeName(type));

        var bases = new List<string>();
        if (type is { IsClass: true, BaseType: not null } && type.BaseType != typeof(object)
            && !typeof(Delegate).IsAssignableFrom(type))
            bases.Add(TypeName(type.BaseType));
        // Only DIRECTLY implemented interfaces — inherited ones are already recorded on the base type,
        // and including them would make an unrelated base-class change churn this file.
        var inherited = type.BaseType?.GetInterfaces() ?? [];
        bases.AddRange(type.GetInterfaces()
            .Where(i => !inherited.Contains(i))
            .Select(TypeName)
            .OrderBy(s => s, StringComparer.Ordinal));
        if (bases.Count > 0) sb.Append(" : ").Append(string.Join(", ", bases));

        sb.Append(RenderConstraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : []));
        if (type.IsEnum) sb.Append(" (").Append(TypeName(type.GetEnumUnderlyingType())).Append(')');
        return sb.ToString();
    }

    // ── members ───────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> RenderMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var member in type.GetMembers(flags))
        {
            switch (member)
            {
                case Type: continue; // nested types get their own top-level entry via GetExportedTypes
                case FieldInfo field when IsVisible(field): yield return RenderField(field); break;
                case PropertyInfo property when IsVisible(property): yield return RenderProperty(property); break;
                case EventInfo evt when IsVisible(evt): yield return RenderEvent(evt); break;
                case MethodInfo method when IsVisible(method) && !IsAccessor(method):
                    yield return RenderMethod(method); break;
                case ConstructorInfo ctor when IsVisible(ctor) && !IsCompilerRequiredMemberStub(ctor):
                    yield return RenderConstructor(ctor); break;
            }
        }
    }

    /// <summary>
    /// Public OR protected — the two visibilities a consumer can reach. `protected internal` counts as
    /// protected from outside the assembly; `private protected` does not (it is internal to us).
    /// </summary>
    private static bool IsVisibleAccess(MethodAttributes access) => access is MethodAttributes.Public
        or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static bool IsVisible(MethodBase method) => IsVisibleAccess(method.Attributes & MethodAttributes.MemberAccessMask);

    private static bool IsVisible(FieldInfo field) => field.Attributes.HasFlag(FieldAttributes.Public)
        || (field.Attributes & FieldAttributes.FieldAccessMask) is FieldAttributes.Family or FieldAttributes.FamORAssem;

    private static bool IsVisible(PropertyInfo property) =>
        (property.GetMethod is { } get && IsVisible(get)) || (property.SetMethod is { } set && IsVisible(set));

    private static bool IsVisible(EventInfo evt) => evt.AddMethod is { } add && IsVisible(add);

    /// <summary>
    /// A type with <c>required</c> members gets a parameterless constructor marked
    /// <c>[Obsolete(..., error: true)]</c> so that OLD compilers cannot bypass the requirement. It is not
    /// callable API, and its message text is SDK-version-dependent — leaving it in would make the
    /// baseline churn on an unrelated toolchain update.
    /// </summary>
    private static bool IsCompilerRequiredMemberStub(ConstructorInfo ctor) =>
        ctor.GetParameters().Length == 0
        && ctor.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(ObsoleteAttribute)
            && a.ConstructorArguments.Count == 2
            && a.ConstructorArguments[1].Value is true
            && a.ConstructorArguments[0].Value is string message
            && message.Contains("required members", StringComparison.Ordinal));

    /// <summary>Property/event accessors are rendered with their member, not as separate lines.</summary>
    private static bool IsAccessor(MethodInfo method) => method.IsSpecialName
        && (method.Name.StartsWith("get_", StringComparison.Ordinal)
            || method.Name.StartsWith("set_", StringComparison.Ordinal)
            || method.Name.StartsWith("add_", StringComparison.Ordinal)
            || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static string RenderField(FieldInfo field)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(field)) sb.Append(attribute).Append(' ');
        sb.Append(Access(field.Attributes & FieldAttributes.FieldAccessMask)).Append(' ');
        if (IsRequired(field)) sb.Append("required ");
        if (field.IsLiteral) sb.Append("const ");
        else
        {
            if (field.IsStatic) sb.Append("static ");
            if (field.IsInitOnly) sb.Append("readonly ");
        }
        sb.Append(TypeName(field, Nullability.Create(field).ReadState)).Append(' ').Append(field.Name);
        // Constant VALUES are part of the contract — a wire code or an error string is what a consumer
        // compares against, so changing one is a break even though the signature is identical.
        if (field.IsLiteral && field.GetRawConstantValue() is { } value)
            sb.Append(" = ").Append(Literal(value));
        return sb.ToString();
    }

    private static string RenderProperty(PropertyInfo property)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(property)) sb.Append(attribute).Append(' ');

        var accessor = property.GetMethod ?? property.SetMethod!;
        sb.Append(Access(accessor.Attributes & MethodAttributes.MemberAccessMask)).Append(' ');
        if (IsRequired(property)) sb.Append("required ");
        if (accessor.IsStatic) sb.Append("static ");
        sb.Append(Virtuality(accessor));
        sb.Append(TypeName(property, Nullability.Create(property).ReadState)).Append(' ').Append(property.Name);

        var indexer = property.GetIndexParameters();
        if (indexer.Length > 0) sb.Append('[').Append(RenderParameters(indexer)).Append(']');

        sb.Append(" { ");
        if (property.GetMethod is { } get && IsVisible(get)) sb.Append(Prefix(get, accessor)).Append("get; ");
        if (property.SetMethod is { } set && IsVisible(set))
            sb.Append(Prefix(set, accessor)).Append(IsInitOnly(set) ? "init; " : "set; ");
        sb.Append('}');
        return sb.ToString();

        // An accessor less visible than the member itself (`public string X { get; protected set; }`)
        // must show its own access, or narrowing one would be invisible.
        static string Prefix(MethodInfo one, MethodInfo primary) =>
            (one.Attributes & MethodAttributes.MemberAccessMask) == (primary.Attributes & MethodAttributes.MemberAccessMask)
                ? "" : Access(one.Attributes & MethodAttributes.MemberAccessMask) + " ";
    }

    private static string RenderEvent(EventInfo evt)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(evt)) sb.Append(attribute).Append(' ');
        var add = evt.AddMethod!;
        sb.Append(Access(add.Attributes & MethodAttributes.MemberAccessMask)).Append(' ');
        if (add.IsStatic) sb.Append("static ");
        sb.Append(Virtuality(add)).Append("event ");
        sb.Append(evt.EventHandlerType is { } handler ? TypeName(handler) : "?").Append(' ').Append(evt.Name);
        return sb.ToString();
    }

    private static string RenderMethod(MethodInfo method)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(method)) sb.Append(attribute).Append(' ');
        sb.Append(Access(method.Attributes & MethodAttributes.MemberAccessMask)).Append(' ');
        if (method.IsStatic) sb.Append("static ");
        sb.Append(Virtuality(method));
        sb.Append(TypeName(method.ReturnParameter, Nullability.Create(method.ReturnParameter).ReadState))
          .Append(' ').Append(method.Name);
        if (method.IsGenericMethodDefinition)
            sb.Append('<').Append(string.Join(", ", method.GetGenericArguments().Select(a => a.Name))).Append('>');
        sb.Append('(').Append(RenderParameters(method.GetParameters())).Append(')');
        sb.Append(RenderConstraints(method.IsGenericMethodDefinition ? method.GetGenericArguments() : []));
        return sb.ToString();
    }

    private static string RenderConstructor(ConstructorInfo ctor)
    {
        var sb = new StringBuilder();
        foreach (var attribute in RenderAttributes(ctor)) sb.Append(attribute).Append(' ');
        sb.Append(Access(ctor.Attributes & MethodAttributes.MemberAccessMask)).Append(' ');
        if (ctor.IsStatic) sb.Append("static ");
        sb.Append(".ctor(").Append(RenderParameters(ctor.GetParameters())).Append(')');
        return sb.ToString();
    }

    private static string RenderParameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(RenderParameter));

    private static string RenderParameter(ParameterInfo parameter)
    {
        var sb = new StringBuilder();
        if (parameter.GetCustomAttributes().Any(a => a.GetType().Name == "ParamArrayAttribute")) sb.Append("params ");
        if (parameter.IsOut) sb.Append("out ");
        else if (parameter.ParameterType.IsByRef) sb.Append(parameter.IsIn ? "in " : "ref ");
        sb.Append(TypeName(parameter, Nullability.Create(parameter).ReadState));
        // Parameter NAMES are a source contract — a consumer may pass named arguments.
        sb.Append(' ').Append(parameter.Name);
        // The DEFAULT is what makes a call site compile; dropping one breaks every caller that omitted it.
        if (parameter.HasDefaultValue) sb.Append(" = ").Append(DefaultValue(parameter));
        return sb.ToString();
    }

    // ── pieces ────────────────────────────────────────────────────────────────────────────────────

    private static string Access(MethodAttributes access) => access switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        _ => access.ToString(),
    };

    private static string Access(FieldAttributes access) => access switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        _ => access.ToString(),
    };

    private static string Virtuality(MethodBase method) => method switch
    {
        { IsAbstract: true } => "abstract ",
        // A non-virtual slot-reusing override is not a thing in C#, so IsVirtual + !NewSlot == override.
        { IsVirtual: true } when !method.Attributes.HasFlag(MethodAttributes.NewSlot) => "override ",
        { IsVirtual: true, IsFinal: false } => "virtual ",
        _ => "",
    };

    private static bool IsRequired(MemberInfo member) =>
        member.GetCustomAttributes().Any(a => a is RequiredMemberAttribute);

    /// <summary>`init` is encoded as an <c>IsExternalInit</c> modreq on the setter's return.</summary>
    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m == typeof(IsExternalInit));

    private static IEnumerable<string> RenderAttributes(MemberInfo member) => RenderAttributes(member.GetCustomAttributesData());

    private static IEnumerable<string> RenderAttributes(IList<CustomAttributeData> attributes) => attributes
        .Where(a => a.AttributeType.Namespace is { } ns
                    && !NoiseAttributeNamespaces.Any(noise => ns.StartsWith(noise, StringComparison.Ordinal))
                    && !NoiseAttributeNames.Contains(a.AttributeType.FullName))
        .Select(a =>
        {
            var name = a.AttributeType.Name;
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            var args = a.ConstructorArguments.Select(x => Literal(x.Value))
                .Concat(a.NamedArguments.Select(n => $"{n.MemberName} = {Literal(n.TypedValue.Value)}"))
                .ToArray();
            return args.Length == 0 ? $"[{name}]" : $"[{name}({string.Join(", ", args)})]";
        })
        .OrderBy(s => s, StringComparer.Ordinal);

    private static string RenderConstraints(Type[] genericArguments)
    {
        var clauses = new List<string>();
        foreach (var argument in genericArguments)
        {
            var parts = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) parts.Add("class");
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) parts.Add("struct");
            parts.AddRange(argument.GetGenericParameterConstraints()
                .Where(c => c != typeof(ValueType))
                .Select(TypeName)
                .OrderBy(s => s, StringComparer.Ordinal));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)) parts.Add("new()");
            if (parts.Count > 0) clauses.Add($"where {argument.Name} : {string.Join(", ", parts)}");
        }
        return clauses.Count == 0 ? "" : " " + string.Join(" ", clauses);
    }

    /// <summary>
    /// A parameter's default, as a C# author would write it.
    /// <para>
    /// Reflection reports <c>default(T)</c> for a non-nullable VALUE type as a null
    /// <c>RawDefaultValue</c>, which the literal renderer would print as <c>= null</c> — and a human
    /// reviews this file on every surface change, so <c>CancellationToken cancellationToken = null</c>
    /// reads as "this parameter is nullable", which is not a thing a struct parameter can be. Print
    /// <c>= default</c> there instead. Reference types keep <c>= null</c>, which is both accurate and
    /// what the source says.
    /// </para>
    /// </summary>
    private static string DefaultValue(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (parameter.RawDefaultValue is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            return "default";
        return Literal(parameter.RawDefaultValue);
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
                            .Replace("\"", "\\\"", StringComparison.Ordinal)
                            .Replace("\0", "\\0", StringComparison.Ordinal) + "\"",
        bool b => b ? "true" : "false",
        char c => "'" + c + "'",
        Type t => "typeof(" + TypeName(t) + ")",
        // Invariant so a comma-decimal machine can't produce a different baseline than a dot-decimal one
        // (this repo has already been bitten by locale-dependent formatting).
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    // ── type names ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Annotate"/> decides whether to add its OWN <c>?</c> by checking <c>IsValueType</c> —
    /// but a <c>ref</c>/<c>out</c> parameter's <see cref="ParameterInfo.ParameterType"/> is the BYREF
    /// wrapper (e.g. <c>System.Double&amp;</c>), which reports <c>IsValueType == false</c> regardless of
    /// what it points to. For an ordinary reference type that coincidentally reads as correct; for a
    /// nullable VALUE type passed by reference (a record's synthesized <c>Deconstruct(out double? Total,
    /// …)</c>, first hit by <c>OperationProgress</c>) it made <see cref="Annotate"/> add a second <c>?</c>
    /// on top of the one <see cref="TypeName(Type)"/>'s own <c>Nullable&lt;T&gt;</c> unwrap already
    /// produced — rendering the invalid <c>double??</c>. Unwrap the byref FIRST, matching what
    /// <see cref="TypeName(Type)"/> itself already does, so <c>Annotate</c> sees the pointee's real
    /// value-type-ness.
    /// </summary>
    private static string TypeName(ParameterInfo parameter, NullabilityState nullability)
    {
        var type = parameter.ParameterType;
        if (type.IsByRef) type = type.GetElementType()!;
        return Annotate(TypeName(parameter.ParameterType), type, nullability);
    }

    private static string TypeName(PropertyInfo property, NullabilityState nullability) =>
        Annotate(TypeName(property.PropertyType), property.PropertyType, nullability);

    private static string TypeName(FieldInfo field, NullabilityState nullability) =>
        Annotate(TypeName(field.FieldType), field.FieldType, nullability);

    /// <summary>
    /// Append `?` for a nullable REFERENCE type. Value types already carry it in their name
    /// (<c>Nullable&lt;T&gt;</c> renders as <c>T?</c>), and an unannotated context reads as Unknown —
    /// which must render as nothing, not as a spurious difference.
    /// <para>
    /// An UNCONSTRAINED type parameter is skipped deliberately: the runtime reports it as Nullable
    /// because <c>T</c> may be instantiated with a nullable type, so annotating it printed
    /// <c>T? RunOrDefault&lt;T&gt;(..., T? fallback, ...)</c> for a signature whose source says plain
    /// <c>T</c> — a difference that does not exist, in the one place a reviewer most needs to trust the
    /// output.
    /// </para>
    /// </summary>
    private static string Annotate(string name, Type type, NullabilityState nullability) =>
        nullability == NullabilityState.Nullable && !type.IsValueType && !type.IsGenericParameter
            ? name + "?" : name;

    /// <summary>Short, stable, C#-shaped type name (no assembly qualification, no backtick arity).</summary>
    private static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!);
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter) return type.Name;
        if (Aliases.TryGetValue(type, out var alias)) return alias;

        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";

        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0) name = name[..tick];
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return type.FullName ?? type.Name;
    }
}
