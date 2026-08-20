namespace Shenora.Engine.Missions;

/// <summary>
/// Shared state flowing along ONE chain, plus where that chain has got to. Handed to every step of
/// the same chain and to nothing else.
/// <para>
/// ⚠ <b>IN MEMORY ONLY</b> — nothing put here survives a restart. A DURABLE chain that resumes carries
/// its state in <see cref="MissionDefinition.Payload"/> instead.
/// </para>
/// </summary>
public interface IMissionChainContext
{
    /// <summary>0-based index of the step now running.</summary>
    int StepIndex { get; }

    /// <summary>Name of the step now running, from <see cref="MissionStep.Name"/>.</summary>
    string StepName { get; }

    /// <summary>Total steps in this chain.</summary>
    int StepCount { get; }

    /// <summary>Read a value an earlier step put here. Default when absent or of another type.</summary>
    T? Get<T>(string key);

    /// <summary>Publish a value for later steps. Overwrites.</summary>
    void Set<T>(string key, T value);
}

/// <summary>
/// One step of a chain: a body, and optionally the resources and retry budget that step alone needs.
/// </summary>
/// <param name="Name">Diagnostic name — appears in the context and in failure messages.</param>
/// <param name="Run">The step body. Gets the chain's execution, its shared context, and the token.</param>
/// <param name="Claims">
/// Claims this step needs. They are folded into the CHAIN's claim set and held for the whole chain,
/// not just this step.
/// </param>
/// <param name="Retry">Retries THIS step. There is no chain-level retry.</param>
public sealed record MissionStep(
    string Name,
    Func<MissionExecution, IMissionChainContext, CancellationToken, Task> Run,
    IReadOnlyList<MissionClaim>? Claims = null,
    RetryPolicy? Retry = null);

/// <summary>
/// Builds a multi-step mission: steps that run in order, where a later one depends on what an earlier
/// one did. <b>A chain is ONE mission, not N</b> — <see cref="Sequence"/> returns an ordinary
/// <see cref="MissionDefinition"/>, so a failing step fails the chain there and one token cancels it.
/// <para>
/// ⚠ <b>The chain holds the UNION of its steps' claims for its whole life</b>, so a five-step chain
/// touching five paths blocks all five from the start.
/// </para>
/// </summary>
public static class MissionChain
{
    /// <summary>
    /// A definition that runs <paramref name="steps"/> in order, sharing one
    /// <see cref="IMissionChainContext"/>, and claiming the union of their claims up front.
    /// </summary>
    /// <param name="kind">App-defined mission type for the whole chain.</param>
    /// <param name="steps">The steps, in order. At least one.</param>
    public static MissionDefinition Sequence(string kind, params MissionStep[] steps)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0)
            throw new ArgumentException("A chain needs at least one step.", nameof(steps));
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentNullException.ThrowIfNull(step.Run);
        }

        return new MissionDefinition
        {
            Kind = kind,
            Claims = UnionOf(steps),
            // No chain-level retry: each step carries its own budget, applied by RunStepAsync.
            Retry = RetryPolicy.None,
            Run = (execution, cancellationToken) => RunStepsAsync(execution, steps, cancellationToken),
        };
    }

    /// <summary>One claim per (scope, key), taking the STRONGER mode when steps disagree.</summary>
    private static IReadOnlyList<MissionClaim> UnionOf(MissionStep[] steps)
    {
        var strongest = new Dictionary<(string Scope, string Key), ClaimMode>();
        foreach (var step in steps)
        {
            foreach (var claim in step.Claims ?? [])
            {
                var key = (claim.Scope, claim.Key);
                if (strongest.TryGetValue(key, out var mode) && mode == ClaimMode.Exclusive) continue;
                strongest[key] = claim.Mode;
            }
        }
        return [.. strongest.Select(pair => new MissionClaim(pair.Key.Scope, pair.Key.Key, pair.Value))];
    }

    private static async Task RunStepsAsync(
        MissionExecution execution, MissionStep[] steps, CancellationToken cancellationToken)
    {
        var context = new ChainContext(steps);
        for (var index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index];
            context.Advance(index);
            await RunStepAsync(execution, context, step, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the step's own retry budget — not the scheduler's, which retries a MISSION and would
    /// re-run the completed steps.
    /// </summary>
    private static async Task RunStepAsync(
        MissionExecution execution, ChainContext context, MissionStep step, CancellationToken cancellationToken)
    {
        var retry = step.Retry ?? RetryPolicy.None;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await step.Run(execution, context, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                attempt < retry.Attempts && !cancellationToken.IsCancellationRequested && retry.IsTransient(ex))
            {
                await Task.Delay(retry.Delay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ChainContext(MissionStep[] steps) : IMissionChainContext
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public int StepIndex { get; private set; }
        public string StepName { get; private set; } = steps[0].Name;
        public int StepCount => steps.Length;

        internal void Advance(int index)
        {
            StepIndex = index;
            StepName = steps[index].Name;
        }

        // Unsynchronized: steps run one after another on the chain's single body.
        public T? Get<T>(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _values.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }

        public void Set<T>(string key, T value)
        {
            ArgumentNullException.ThrowIfNull(key);
            _values[key] = value;
        }
    }
}
