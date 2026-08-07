namespace Shenora.Ipc;

/// <summary>
/// Tracks long-running work started by any module: id, owning module, app-defined kind and scope,
/// status, progress, timestamps. Mechanism only — the kit ships no queue, scheduler, retry,
/// priority, or phase model, and no opinion on what an operation IS or means. See
/// <see cref="OperationEvents"/> for the event this registry publishes on every transition.
/// </summary>
public interface IOperationRegistry
{
    /// <summary>
    /// Start a tracked operation owned by <paramref name="module"/> and get its handle. Publishes
    /// an immediate <see cref="OperationStatus.Running"/> snapshot.
    /// <para>
    /// <b>"Registered but not yet started" is representable with no kit change</b> (closes what a
    /// generic-library audit had recorded as a known limit): the FIRST snapshot is still
    /// <see cref="OperationStatus.Running"/>, but an app with its own queue in front of this registry
    /// can immediately call <see cref="IOperation.Wait"/>(<c>"queued"</c>) on the returned handle
    /// before any real work begins — the same mechanism a mid-flight blocker uses, with the app's own
    /// reason string standing in for a queued/pending status the kit never needed to add. Nothing was
    /// ever progressing in either case, so <c>Wait</c> reads correctly for both.
    /// </para>
    /// </summary>
    IOperation Start(string module, OperationOptions options);

    /// <summary>
    /// Start the operation, hand <paramref name="work"/> OFF to the background, and finish it:
    /// <c>Complete</c> on success, <c>Cancel</c> on <see cref="OperationCanceledException"/>,
    /// <c>Fail</c> otherwise (never a raw exception — see the type doc). Returns the operation id
    /// IMMEDIATELY.
    /// <para>
    /// This is the primitive; <see cref="IModuleContext.Run"/> is a one-line delegation onto it. It
    /// lives HERE, not on the context, so it is reachable from ordinary services too — a download
    /// service starting an installer fetch, not only an IPC facade route. One implementation,
    /// reachable from both call-site shapes.
    /// </para>
    /// <para>
    /// The work gets the OPERATION's own token, never a caller's — see
    /// <see cref="IOperation.CancellationToken"/>: work handed off outlives whatever started it, and
    /// capturing a caller's token (a request, say) kills it the moment that caller's lifetime ends.
    /// </para>
    /// <para>
    /// <b>Waiting by returning</b> (§5A.3): <paramref name="work"/> can call <c>op.Wait(reason)</c>
    /// and simply RETURN instead of throwing — <c>Complete</c> is only ever applied implicitly when
    /// the operation is STILL <see cref="OperationStatus.Running"/> once <paramref name="work"/>
    /// finishes, so a body that waited and returned is left <see cref="OperationStatus.Waiting"/>, not
    /// silently stamped <see cref="OperationStatus.Completed"/>. Resuming it from there is the APP's
    /// job (the same handle's <c>op.Resume()</c>, or its own checkpoint/restart path) — see
    /// <see cref="IModuleContext.Run"/>'s own doc for the full rationale.
    /// </para>
    /// </summary>
    string Run(string module, OperationOptions options, Func<IOperation, CancellationToken, Task> work);

    /// <summary>
    /// Resolve a live handle for an already-started operation by id — reinstated (generic-library
    /// audit finding 3) after being sketched-then-dropped pre-0.2.0 as unearned surface ("no consumer
    /// resolves a handle from a bare id"). That ruling did not survive contact with
    /// <see cref="RequestWait"/>/<see cref="RequestResume"/>: both are client-request routes that
    /// carry only an id, and whoever handles them (the owning module, hearing
    /// <see cref="OperationEvents.WaitRequested"/>/<see cref="OperationEvents.ResumeRequested"/>) must
    /// translate that id back into a handle to call <see cref="IOperation.Wait"/>/
    /// <see cref="IOperation.Resume"/> — a recurring shape every such consumer would otherwise
    /// re-solve with its own id→handle map kept alongside the registry. Returns <c>null</c> for an
    /// unknown id.
    /// <para>
    /// Safe to hold past the operation's life: every <see cref="IOperation"/> member re-validates the
    /// entry's CURRENT status before acting (the same <c>Validate</c> gate every other transition goes
    /// through), so a handle resolved here for an entry that later finishes — or looked up again after
    /// it already has — is a no-op on every subsequent call, never a dangling reference the caller
    /// must guard.
    /// </para>
    /// </summary>
    IOperation? Find(string id);

    /// <summary>
    /// Snapshot of currently-known operations (running plus retained finished history, capped by
    /// <see cref="OperationRegistryOptions.MaxHistory"/>), optionally filtered by owning module
    /// and/or scope. Running operations sort first.
    /// <para>
    /// <paramref name="scope"/> follows the SAME rule as <see cref="Shenora.IEventBus"/>'s scope
    /// matching, not strict equality: <c>null</c> (the default) returns every scope, and an operation
    /// started with no <see cref="OperationOptions.Scope"/> of its own matches ANY requested scope —
    /// a scope-less operation is global, the same way a scope-less event reaches scoped subscribers.
    /// </para>
    /// </summary>
    IReadOnlyList<OperationInfo> GetAll(string? module = null, string? scope = null);

    /// <summary>
    /// Cancel a running operation by id: cancels its own <see cref="CancellationToken"/> first,
    /// then transitions it to <see cref="OperationStatus.Cancelled"/>. Returns false — and changes
    /// nothing — for an unknown id, one that has already reached a terminal state, or one started
    /// with <see cref="OperationOptions.Cancellable"/> false: that flag is documented as "exposes a
    /// WORKING cancel", so honoring a cancel the operation never opted into would flip its status
    /// while the body kept running to completion underneath it.
    /// </summary>
    bool Cancel(string id);

    /// <summary>
    /// Drop finished (terminal) history, keeping running (and WAITING-band) work untouched.
    /// Filtered EXACTLY like <see cref="GetAll"/> — same two keys, same rule (<paramref name="scope"/>
    /// follows <see cref="Shenora.IEventBus"/>'s scope matching: <c>null</c> clears every scope,
    /// and an unscoped finished entry is cleared by ANY requested scope).
    /// <para>
    /// Was unfilterable for one release (generic-library audit finding 1): the kit ships secondary
    /// windows and a scoped container router, so "clear completed" in one scoped window used to wipe
    /// every OTHER scope's finished history too — the read side (<see cref="GetAll"/>) had the filter
    /// from day one; the removal side did not.
    /// </para>
    /// </summary>
    void ClearFinished(string? module = null, string? scope = null);

    /// <summary>
    /// Decline a pending offer in the WAITING band (§5A.2): <see cref="OperationStatus.Waiting"/> →
    /// <see cref="OperationStatus.Cancelled"/> — terminal, so it enters bounded history and is
    /// prunable/clearable like any other finished entry, and publishes an
    /// <see cref="OperationEvents.Updated"/> snapshot like any other terminal transition (unlike
    /// <see cref="ClearFinished"/>/<see cref="RequestResume"/>, which remove an entry with no
    /// corresponding wire event).
    /// <para>
    /// <b>Refuses <see cref="OperationStatus.Running"/></b> — returns <c>false</c> and changes nothing.
    /// Declining a pending offer and cancelling LIVE work are different acts: cancelling is
    /// <see cref="Cancel"/>'s job, permission-checked against <see cref="OperationOptions.Cancellable"/>;
    /// routing around that check by letting <c>Dismiss</c> also accept <see cref="OperationStatus.Running"/>
    /// is the exact conflation that produced this branch's only Critical (§5A.3). A separate member,
    /// not <c>Cancel</c> accepting more states, for the same reason.
    /// </para>
    /// <para>
    /// Signals the entry's own <see cref="CancellationToken"/> FIRST, so a waiting body still parked on
    /// its token unwinds the same way a running one does under <see cref="Cancel"/> — a
    /// <see cref="OperationStatus.Waiting"/> entry always has a live token, because the only way to
    /// reach that status is <see cref="IOperation.Wait"/> on a handle <see cref="Start"/> produced.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>false</c> — and changes nothing — for an unknown id, a <see cref="OperationStatus.Running"/>
    /// one, or one already terminal: the same honest-refusal shape as every other transition here.
    /// </returns>
    bool Dismiss(string id);

    /// <summary>
    /// The user asked to resume operation <paramref name="id"/> — the exact mirror of
    /// <see cref="RequestWait"/> for the other direction. Accepts <see cref="OperationStatus.Waiting"/>
    /// only, returning <c>false</c> and changing nothing otherwise (an unknown id, a
    /// <see cref="OperationStatus.Running"/> one, or a terminal one — the same honest-refusal shape as
    /// every other transition here). On success, emits <see cref="OperationEvents.ResumeRequested"/>
    /// with <c>{ operationId, module, kind, scope }</c> and leaves the entry untouched: **the client
    /// asking is not the state changing** — the owning module's OWN <see cref="IOperation.Resume"/> is
    /// what restarts the work and publishes the transition.
    /// <para>
    /// <b>This used to be asymmetric, and un-asymmetring it is the 0.2.0 design pass (D1).</b> The
    /// registry once also accepted crash-checkpoint entries the kit had never started
    /// (<c>RegisterWaiting</c> + <c>OperationOptions.ResumePayload</c>), and this method REMOVED those
    /// while keeping live ones — so every call had to answer "does this entry still have a body?".
    /// That question produced a stranded state (a status with no terminal exit), then a bug that
    /// dropped genuinely live operations, then an internal provenance flag to paper over it. The
    /// checkpoint half is now cut: crash recovery is the APP's business — it owns the checkpoint, and a
    /// resumed run is simply a fresh <see cref="Start"/>/<see cref="Run"/>. Every entry here reached
    /// <see cref="OperationStatus.Waiting"/> through a live <see cref="IOperation.Wait"/>, so there is
    /// always a handle to flip and nothing is ever removed.
    /// </para>
    /// </summary>
    bool RequestResume(string id);

    /// <summary>
    /// The user asked to wait operation <paramref name="id"/> — generic-library audit finding 3
    /// (renamed from <c>RequestPause</c>), an EXACT mirror of <see cref="RequestResume"/> for the other
    /// direction. Accepts only <see cref="OperationStatus.Running"/>, returning <c>false</c> and
    /// changing nothing otherwise (unknown id, already <see cref="OperationStatus.Waiting"/>, or
    /// terminal — the same honest-refusal shape as every other transition here). On success, emits
    /// <see cref="OperationEvents.WaitRequested"/> with <c>{ operationId, module, kind, scope }</c>
    /// and leaves the entry untouched — the owning module's OWN <see cref="IOperation.Wait"/> is what
    /// actually flips the status once it has stopped. **The client asking is not the state
    /// changing** — the same split <see cref="RequestResume"/> already draws against
    /// <see cref="IOperation.Resume"/>, applied to the direction the kit previously had no client
    /// route for at all ("pausing is the host's own knowledge" is true for a host discovering its OWN
    /// blocker, not for the equally-common shape of a human clicking Pause on visible work — a
    /// download, a sync, a backup — that the kit itself already names as a consumer).
    /// </summary>
    bool RequestWait(string id);
}
