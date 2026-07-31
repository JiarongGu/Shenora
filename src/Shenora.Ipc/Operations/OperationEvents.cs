namespace Shenora.Ipc;

/// <summary>Event and request type names for the operations module. Constants, so an app matches by
/// symbol rather than by a literal that a rename cannot follow.</summary>
public static class OperationEvents
{
    /// <summary>A full <see cref="OperationInfo"/> snapshot — every transition uses this one type,
    /// so folding is last-write-wins by id with no cross-type ordering hazard.</summary>
    public const string Updated = "OPERATION_UPDATED";

    /// <summary>An interrupted+resumable operation should be continued by its owning module.</summary>
    public const string ResumeRequested = "OPERATION_RESUME_REQUESTED";
}
