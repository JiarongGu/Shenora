namespace Shenora.Engine;

/// <summary>
/// How a failed attempt is retried. Shared by the mission scheduler and the file-update queue (D30);
/// defaults are tuned for transient filesystem locks held by an external process.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Total attempts including the first. 1 = no retry. Default 3.</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>Base delay, multiplied by the attempt number (500ms, 1s, 1.5s…). Default 500ms.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Whether a failure is worth retrying. Default: retry <see cref="IOException"/> only —
    /// retrying a <see cref="NullReferenceException"/> three times just delays the report.
    /// </summary>
    public Func<Exception, bool> IsTransient { get; init; } = static ex => ex is IOException;

    /// <summary>A policy that never retries.</summary>
    public static RetryPolicy None { get; } = new() { Attempts = 1 };
}
