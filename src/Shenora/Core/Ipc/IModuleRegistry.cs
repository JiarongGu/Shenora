namespace Shenora.Core.Ipc;

/// <summary>
/// The lifecycle of the modules a dispatcher routes — claim, ask, release. The seam an app needs
/// before mapping a module it did not write itself (plug-ins, licence-gated features, per-tenant or
/// lazily loaded modules), because a mapping that silently shadowed an earlier one would let a module
/// take over another's channel.
/// <para>
/// Kept OFF <see cref="IMessageDispatcher"/>, which stays the four things a dispatcher IS. A decorator
/// wanting this seam implements this interface too and forwards every member; ⚠ one that does not is
/// treated as "cannot answer" rather than "nothing is mapped", because the permissive answer is the
/// dangerous one here.
/// </para>
/// <para>
/// CLAIM AND RELEASE ARE ONE OWNER'S JOB, which is why <see cref="TryClaimModule"/> takes the facade
/// rather than just recording a name: the registry has to hold the routing it installed in order to
/// take it out again.
/// </para>
/// </summary>
public interface IModuleRegistry
{
    /// <summary>Every module name currently claimed, in no particular order.</summary>
    IReadOnlyCollection<string> MappedModules { get; }

    /// <summary>True when <paramref name="module"/> is currently claimed (case-insensitive, as routing is).</summary>
    bool IsModuleMapped(string module);

    /// <summary>
    /// Claim <paramref name="facade"/>'s module name and start routing to it, unless the name is
    /// already taken. Called by <c>MapModule</c>/<c>TryMapModule</c>; apps normally use those.
    /// </summary>
    /// <returns>True if the module was claimed; false if the name was already in use.</returns>
    bool TryClaimModule(IIpcModule facade);

    /// <summary>
    /// Release a claimed module: it stops answering and its name becomes free to claim again. Requests
    /// ALREADY executing inside that facade run to completion, and the facade is NOT disposed — its
    /// lifetime belongs to whoever created it.
    /// </summary>
    /// <returns>True if the module was claimed and is now released; false if it was not claimed.</returns>
    bool TryReleaseModule(string module);
}
