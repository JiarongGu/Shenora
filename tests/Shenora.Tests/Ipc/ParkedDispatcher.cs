using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>Parks every dispatch until <see cref="Gate"/> is completed, and records the token it was handed.</summary>
internal sealed class ParkedDispatcher : IMessageDispatcher
{
    public CancellationToken Seen { get; private set; }

    public TaskCompletionSource<IpcResponse> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        Seen = cancellationToken;
        return Gate.Task;
    }

    public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                       CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                                 CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IMessageDispatcher Use(MessageMiddleware middleware) => this;
}
