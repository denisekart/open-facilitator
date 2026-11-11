namespace Facilitator.Kernel.Work;

/// <summary>Read projection of a Work for API responses (a superset of the
/// original seven-column record: the new fields read as null on old rows).
/// <c>ActiveWhere</c> is the work's provider-plane residence, served as
/// parsed jsonb text.</summary>
/// <param name="WorkId">Work identity.</param>
/// <param name="ProjectId">Owning project when bound.</param>
/// <param name="Objective">Objective text.</param>
/// <param name="State">Wire state name.</param>
/// <param name="Version">Current fencing version.</param>
/// <param name="TeamSlug">Owning team (own <c>team_id</c> or the project's team).</param>
/// <param name="OwnerActor">Accountable actor (<c>oidc:iss|sub</c> / <c>contributor:handle</c>); null = team attention.</param>
/// <param name="Origin">First-delivered wire type (origin disclosure), null when unknown.</param>
/// <param name="ActiveWhere">Current residence jsonb, null when unset.</param>
/// <param name="Cost">Cost jsonb as reported by an executor, null when unset.</param>
/// <param name="MergedInto">Survivor work when this one was merged away.</param>
/// <param name="CloseReason">Recorded closure basis, null while open or closed with evidence.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last mutation time (UTC).</param>
public sealed record WorkView(
    Guid WorkId,
    Guid? ProjectId,
    string Objective,
    string State,
    long Version,
    string? TeamSlug,
    string? OwnerActor,
    string? Origin,
    string? ActiveWhere,
    string? Cost,
    Guid? MergedInto,
    string? CloseReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Timeline projection of one <c>work_events</c> row.</summary>
/// <param name="EventId">Event identity.</param>
/// <param name="WorkId">Owning work.</param>
/// <param name="WorkVersion">Work version after this event applied.</param>
/// <param name="Kind">Event kind.</param>
/// <param name="Actor">Actor identity.</param>
/// <param name="Detail">jsonb detail document.</param>
/// <param name="CorrelationId">Correlation id when present.</param>
/// <param name="CreatedAt">Append time (UTC).</param>
public sealed record WorkEventRow(Guid EventId, Guid WorkId, long WorkVersion, string Kind, string Actor, string Detail, string? CorrelationId, DateTime CreatedAt);

/// <summary>Outcome of <see cref="ICoordinationStore.ApplyTransitionAsync"/>.</summary>
public abstract record TransitionResult
{
    /// <summary>Transition applied; carries the reloaded work.</summary>
    public sealed record Ok(WorkView Work) : TransitionResult;

    /// <summary>Version fence failed; caller must rehydrate.</summary>
    public sealed record Conflict(long CurrentVersion) : TransitionResult;

    /// <summary>Edge not in the guard table; message names the edge.</summary>
    public sealed record Illegal(string Edge) : TransitionResult;

    /// <summary>No such work.</summary>
    public sealed record NotFound : TransitionResult;

    /// <summary>A completion on the partially-complete basis must name a
    /// successor work — without one the closure has no evidence trail.</summary>
    public sealed record SuccessorRequired : TransitionResult;

    /// <summary>A completion with no artifact, manifest, work-link or close
    /// reason: an unevidenced claim, refused.</summary>
    public sealed record Unevidenced : TransitionResult;
}

/// <summary>Origin disclosure for a created Work: the wire type that first
/// delivered it, the owning team, the accountable actor, and the residence
/// (jsonb <c>{provider,channel,root,kind}</c>). Every field is what the ingress
/// observed — never inferred.</summary>
public sealed record WorkOrigin(string? WireType = null, Guid? TeamId = null, string? OwnerActor = null, string? ActiveWhere = null);

/// <summary>Optional companions of a Work transition, recorded in the SAME
/// transaction as the state change.</summary>
/// <param name="SuccessorWorkId">Follow-up work carrying the remainder (required for the partial basis).</param>
/// <param name="Basis">Completion basis: <c>complete</c> (default) or <c>partially-complete</c>.</param>
/// <param name="Note">Human-facing note naming what the successor carries.</param>
/// <param name="CloseReason">First-class closure basis; one of the evidence kinds a completion may rest on.</param>
public sealed record TransitionOptions(Guid? SuccessorWorkId = null, string Basis = "complete", string? Note = null, string? CloseReason = null);

/// <summary>One work-link row as served by <see cref="ICoordinationStore.LinksAsync"/>.</summary>
public sealed record WorkLinkRow(Guid LinkId, Guid WorkId, Guid RefWorkId, string Kind, string? Note, DateTime CreatedAt);

/// <summary>Outcome of <see cref="ICoordinationStore.MergeAsync"/>.</summary>
public abstract record MergeResult
{
    /// <summary>Merged; carries the survivor.</summary>
    public sealed record Merged(WorkView Into) : MergeResult;

    /// <summary>No such work (either side).</summary>
    public sealed record NotFound : MergeResult;

    /// <summary>Merging a work into itself, or an already-merged survivor chain loop.</summary>
    public sealed record Loop : MergeResult;
}

/// <summary>Outcome of <see cref="ICoordinationStore.ApplySteerAsync"/>.</summary>
public abstract record SteerResult
{
    /// <summary>Steer applied; carries the reloaded work.</summary>
    public sealed record Ok(WorkView Work) : SteerResult;

    /// <summary>Version fence failed.</summary>
    public sealed record Conflict(long CurrentVersion) : SteerResult;

    /// <summary>No such work.</summary>
    public sealed record NotFound : SteerResult;

    /// <summary>Work is terminal; steer rejected (422 at the edge).</summary>
    public sealed record Terminal : SteerResult;

    /// <summary>commandId already processed upstream (inbox dedupe); store not consulted.</summary>
    public sealed record Duplicate : SteerResult;
}

/// <summary>
/// The durable coordination seam: every mutation is ONE transaction —
/// per-Work advisory xact lock → guard check → version-checked UPDATE →
/// work_events append → outbox append → NOTIFY → commit. Sinks/providers are
/// only ever called after commit, by the dispatcher.
/// </summary>
public interface ICoordinationStore
{
    /// <summary>Create a Work at <c>open</c> version 1 with its <c>created</c> timeline row and outbox progress event.
    /// When <paramref name="assignments"/> is non-empty, each entry appends a routing intent effect in the same
    /// transaction (the funnel owns everything after); legacy callers pass none and get no dispatch.</summary>
    Task<WorkView> CreateWorkAsync(string objective, Guid? projectId, string actor, CancellationToken ct, IReadOnlyList<RoutingIntentSeed>? assignments = null, WorkOrigin? origin = null);

    /// <summary>Operator re-dispatch: a fresh routing intent for an EXISTING
    /// Work (restart-with-context / retry legs). <paramref name="idempotencyKey"/>
    /// keys the effect row; null ⇒ fresh identity. Unknown work ⇒ false.</summary>
    Task<bool> RedispatchAsync(Guid workId, RoutingIntentSeed seed, string? idempotencyKey, CancellationToken ct);

    /// <summary>Guard-checked, version-fenced state transition.</summary>
    Task<TransitionResult> ApplyTransitionAsync(Guid workId, WorkState to, long expectedVersion, string actor, string? reason, CancellationToken ct, TransitionOptions? options = null);

    /// <summary>Version-fenced steer; bumps the work version and appends a steer timeline row + outbox progress event.</summary>
    Task<SteerResult> ApplySteerAsync(Guid workId, string directive, string commandId, long expectedVersion, CancellationToken ct);

    /// <summary>Current projection; null when absent.</summary>
    Task<WorkView?> GetAsync(Guid workId, CancellationToken ct);

    /// <summary>Typed relations of a work (both directions folded: rows where it is the subject, plus its inbound links).</summary>
    Task<IReadOnlyList<WorkLinkRow>> LinksAsync(Guid workId, CancellationToken ct);

    /// <summary>Merge a duplicate into its survivor: the loser keeps its history, gains a
    /// <c>merged-duplicate</c> link and <c>merged_into</c>; reads of the loser answer 303.</summary>
    Task<MergeResult> MergeAsync(Guid workId, Guid intoWorkId, string? reason, string actor, CancellationToken ct);

    /// <summary>Record the executor-reported cost (jsonb) on a work.</summary>
    Task SetCostAsync(Guid workId, string costJson, CancellationToken ct);

    /// <summary>Timeline rows ascending by (created_at, event_id).</summary>
    Task<IReadOnlyList<WorkEventRow>> TimelineAsync(Guid workId, CancellationToken ct);
}
