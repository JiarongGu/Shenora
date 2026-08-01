namespace Shenora.Core;

/// <summary>
/// Shared state flowing along ONE chain, plus where that chain has got to. Handed to every step of
/// the same chain and to nothing else.
///
/// <para>
/// <b>In memory only, deliberately.</b> It exists to pass a temp path from step 1 to step 2 inside one
/// run. A DURABLE chain that resumes after a restart carries its state in
/// <see cref="MissionDefinition.Payload"/> like any other durable mission, because an arbitrary object
/// graph is exactly what the kit cannot serialize on an app's behalf. A resume that silently lost the
/// context would be worse than one that never had it, so the limit is stated rather than papered over.
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
/// not just this step — see <see cref="MissionChain.Sequence"/> for why, and what it costs.
/// </param>
/// <param name="Retry">Retries THIS step. There is no chain-level retry; see the remarks on <see cref="MissionChain"/>.</param>
public sealed record MissionStep(
    string Name,
    Func<MissionExecution, IMissionChainContext, CancellationToken, Task> Run,
    IReadOnlyList<MissionClaim>? Claims = null,
    RetryPolicy? Retry = null);

/// <summary>
/// Builds a multi-step mission: steps that run in order, where a later one depends on what an earlier
/// one did.
///
/// <para>
/// <b>A chain is ONE mission, not N.</b> <see cref="Sequence"/> returns an ordinary
/// <see cref="MissionDefinition"/> that <see cref="IMissionScheduler.SubmitAsync"/> cannot tell apart
/// from any other — so the scheduler gains no concept of dependencies, no "blocked on a predecessor"
/// state, and no edges. That was the fork: N entries with dependency edges would let unrelated steps
/// interleave, but it is a DAG engine by another name, and the kit declined one on the evidence that
/// no sibling has ever needed it.
/// </para>
///
/// <para>
/// <b>What that costs, stated plainly:</b> the chain holds the UNION of its steps' claims for its
/// whole life. A five-step chain touching five paths blocks all five from the start, even during the
/// step that only touches the first. In exchange, claims are still acquired as one set up front, so
/// the deadlock-freedom property is exactly the one the scheduler already guarantees. If a chain's
/// claim union is too coarse for real throughput, that is the evidence for per-step claims — and it
/// is a different design, not a tweak to this one.
/// </para>
///
/// <para>
/// <b>Failure and cancellation:</b> a failing step fails the chain at that step, and later steps do
/// not run. Cancelling cancels the chain — one mission, one token, one cancel. A step's own
/// <see cref="RetryPolicy"/> retries THAT step; there is no chain-level retry, because re-running
/// completed steps is a judgement only the app can make, and it can make it by submitting again.
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
            // No chain-level retry: each step carries its own budget and RunStepsAsync applies it.
            Retry = RetryPolicy.None,
            Run = (execution, cancellationToken) => RunStepsAsync(execution, steps, cancellationToken),
        };
    }

    /// <summary>
    /// One claim per (scope, key), taking the STRONGER mode when steps disagree: a chain that reads a
    /// path in one step and writes it in another must hold it exclusively, or the write would run
    /// alongside another mission's write while this chain merely "had a shared claim".
    /// </summary>
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
    /// The step's own retry budget. Deliberately a copy of the scheduler's rule rather than a call
    /// into it: the scheduler retries a MISSION, and a chain is one mission — if this delegated
    /// upward, a failing step 4 would re-run steps 1 to 3.
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

        // Steps run one after another on the chain's single body, so there is no concurrent access to
        // guard against — the lock that would look prudent here would only be cargo.
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
