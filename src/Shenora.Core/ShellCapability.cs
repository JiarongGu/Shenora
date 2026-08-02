namespace Shenora.Core;

/// <summary>
/// How a shell reports a contract it cannot honour. Owner decision, 2026-08-02: an unsupported
/// capability THROWS, naming the platform — it does not silently no-op.
/// <para>
/// The reasoning is the kit's own precedent rather than taste. A silent no-op is the
/// "mistyped resource prefix degrading to an all-404 provider" bug class this repo keeps paying for:
/// the app looks fine, does nothing, and nothing anywhere says why. <c>ModuleContext.Publish</c>
/// already fails loud and names the exact fix when its dependency was never supplied; this is the
/// same rule applied across shells.
/// </para>
/// <para>
/// <b>Use it only for capabilities that are genuinely ABSENT</b> — native drop zones, a tray icon,
/// secondary windows on a phone. A capability the platform satisfies ITS OWN way is not absent, and
/// throwing there would be wrong: a mobile picker is already modal, so
/// <see cref="IUiInteraction"/>'s block/unblock is honestly a no-op on that shell, documented as one.
/// Absent means "no expression of this exists here", not "we did it differently".
/// </para>
/// <para>
/// <b>Deliberately not a proxy.</b> The obvious implementation — one <c>DispatchProxy</c> that throws
/// for any interface — is reflection, which is exactly what iOS (Mono AOT + trimming) strips and what
/// <c>Shenora.Ipc</c>'s <c>IpcJson.AddTypeInfoResolver</c> seam exists to avoid depending on. A shell
/// writes a small explicit stub per contract it lacks and shares this message instead.
/// </para>
/// </summary>
public static class ShellCapability
{
    /// <summary>
    /// The exception an unsupported capability throws. <paramref name="capability"/> is what the
    /// caller asked for, <paramref name="shell"/> is the host that cannot do it, and
    /// <paramref name="alternative"/> — when there is one — is what to do instead, because an error
    /// that only says "no" leaves the caller exactly where it started.
    /// </summary>
    public static NotSupportedException NotSupported(string capability, string shell, string? alternative = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        var message = $"{capability} is not available on {shell}.";
        if (!string.IsNullOrWhiteSpace(alternative)) message += $" {alternative}";
        return new NotSupportedException(message);
    }
}
