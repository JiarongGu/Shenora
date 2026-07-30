namespace Shenora.Ipc;

/// <summary>
/// The lifecycle of the modules a dispatcher routes — claim, ask, release. The seam an app needs
/// before mapping a module it did not write itself.
/// <para>
/// It exists because module OWNERSHIP was implicit. Nothing recorded that a name was taken, so
/// mapping the same module twice was silent: the second facade simply never ran, with no error and
/// nothing to grep for. Any app that maps modules DYNAMICALLY hits this — plug-ins, optional
/// features behind a licence or flag, per-tenant modules, lazily loaded areas — and for a module
/// coming from outside the app it is a boundary question, not a tidiness one: a late mapping that
/// silently shadowed an earlier one would let it take over another module's channel.
/// </para>
/// <para>
/// Kept OFF <see cref="IMessageDispatcher"/> deliberately. That interface is the four things a
/// dispatcher IS — dispatch, two sends, compose — so a decorator has four members to write and every
/// helper composes for free. A decorator that wants this seam implements this interface too and
/// forwards every member; one that does not is treated as "cannot answer" rather than as
/// "nothing is mapped", because a permissive wrong answer is the dangerous one here.
/// </para>
/// <para>
/// CLAIM AND RELEASE ARE ONE OWNER'S JOB, which is why <c>TryClaimModule</c> takes the facade rather
/// than just recording a name. The registry has to hold the routing it installed in order to be able
/// to take it out again; splitting "remember the name" from "install the route" is what made release
/// impossible in the first place.
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
    bool TryClaimModule(IModuleFacade facade);

    /// <summary>
    /// Release a claimed module: it stops answering and its name becomes free to claim again.
    /// <para>
    /// Requests ALREADY executing inside that facade run to completion — releasing removes the route,
    /// it does not abort work in flight. And the facade is NOT disposed: its lifetime belongs to
    /// whoever created it (usually the DI container), and disposing something the kit does not own is
    /// how a shared instance dies under one caller's feet.
    /// </para>
    /// </summary>
    /// <returns>True if the module was claimed and is now released; false if it was not claimed.</returns>
    bool TryReleaseModule(string module);
}
