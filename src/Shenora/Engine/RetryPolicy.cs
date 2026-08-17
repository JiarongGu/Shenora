namespace Shenora.Engine;

/// <summary>
/// How a failed attempt is retried. Defaults match the value the family's planners independently
/// settled on for transient filesystem locks held by an external process.
/// <para>
/// ⚠ In <c>Shenora.Engine</c> rather than beside either engine that uses it. Both the mission scheduler
/// and the file-update queue apply it with the same loop, and it names no vocabulary from either —
/// while living in <c>Engine.Missions</c> it forced an app using ONLY the file queue to write
/// <c>using Shenora.Engine.Missions;</c> to name its retry policy, against a design whose whole point
/// (D30) is that the two components compose and "neither knows about the other".
/// </para>
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
