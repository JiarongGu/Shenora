namespace Shenora.Core.Ipc;

/// <summary>A route's answer as the wire carries it, whichever way the route was mapped.</summary>
internal static class RouteAnswer
{
    /// <summary>
    /// The answer itself, never a task. A task is an <c>object</c>, so it compiles as an answer: an async route's
    /// <c>return Done();</c> hands over a <c>Task&lt;object?&gt;</c>, which is awaited here, and any other task is work
    /// the route never awaited, which may still be running, so it is an error rather than the task's own fields on
    /// the wire.
    /// </summary>
    public static async ValueTask<object?> SettleAsync(object? data, IpcRequest request)
    {
        // No ConfigureAwait(false): the dispatch path keeps its synchronization context.
        if (data is Task<object?> nested) data = await nested;
        if (data is Task)
            throw new InvalidOperationException($"{request.Module}.{request.Type} answered with a Task instead of its result: await it.");
        return data;
    }
}
