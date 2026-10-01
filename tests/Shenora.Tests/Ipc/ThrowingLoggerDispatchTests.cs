using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

/// <summary>
/// A logger is app code. The failure path logs from inside the catch that keeps <c>DispatchAsync</c> from throwing, so a
/// logger that threw there escaped it; and logging must never change a request's answer.
/// </summary>
public class ThrowingLoggerDispatchTests
{
    [Theory]
    [InlineData("unknown", IpcErrorCodes.UnknownError)]
    [InlineData("operation", "APP_REFUSED")]
    [InlineData("cancelled", IpcErrorCodes.OperationCancelled)]
    public async Task A_failing_handler_still_answers_with_its_error(string failure, string code)
    {
        Exception thrown = failure switch
        {
            "operation" => new ShenoraException("APP_REFUSED"),
            "cancelled" => new OperationCanceledException(),
            _ => new InvalidOperationException("boom"),
        };
        var dispatcher = new MessageDispatcher(new ThrowingLogger<MessageDispatcher>());
        dispatcher.UseRoute("APP", "FAIL", (_, _) => throw thrown);

        var response = await dispatcher.DispatchAsync(IpcRequests.Create("APP", "FAIL", null, null));

        Assert.False(response.Success);
        Assert.Equal(code, response.Error?.Code);
    }

    [Fact]
    public async Task UseLogging_never_changes_the_answer()
    {
        var dispatcher = new MessageDispatcher()
            .UseLogging(new ThrowingLogger<MessageDispatcher>())
            .MapRoute("APP", "PING", _ => "pong");

        Assert.True((await dispatcher.DispatchAsync(IpcRequests.Create("APP", "PING", null, null))).Success);
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("the logger failed");
    }
}
