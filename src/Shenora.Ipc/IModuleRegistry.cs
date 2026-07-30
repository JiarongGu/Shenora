namespace Shenora.Ipc;

/// <summary>
/// What modules a dispatcher already routes — the seam an app needs before mapping a module it did
/// not write itself.
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
/// forwards all three members; one that does not is treated as "cannot answer" rather than as
/// "nothing is mapped", because a permissive wrong answer is the dangerous one here.
/// </para>
/// </summary>
public interface IModuleRegistry
{
    /// <summary>Every module name claimed through <c>MapModule</c>, in no particular order.</summary>
    IReadOnlyCollection<string> MappedModules { get; }

    /// <summary>True when <paramref name="module"/> is already claimed (case-insensitive, as routing is).</summary>
    bool IsModuleMapped(string module);

    /// <summary>
    /// Record that <paramref name="module"/> is claimed. Called by <c>MapModule</c>; apps do not call
    /// this directly, but a DECORATOR must forward it or the registry it exposes goes stale.
    /// </summary>
    void TrackMappedModule(string module);
}
