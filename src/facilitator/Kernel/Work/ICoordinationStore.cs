using Facilitator.Kernel.Domain;

namespace Facilitator.Kernel.Persistence;

/// <summary>Read projection of a Work for API responses.</summary>
/// <param name="WorkId">Work identity.</param>
/// <param name="ProjectId">Owning project when bound.</param>
/// <param name="Objective">Objective text.</param>
/// <param name="State">Wire state name.</param>
/// <param name="Version">Current fencing version.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last mutation time (UTC).</param>
public sealed record WorkView(Guid WorkId, Guid? ProjectId, string Objective, string State, long Version, DateTime CreatedAt, DateTime UpdatedAt);

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

    /// <summary>Version fence failed; caller must rehydrate (spec §7.4).</summary>
    public sealed record Conflict(long CurrentVersion) : TransitionResult;

    /// <summary>Edge not in the guard table; message names the edge.</summary>
    public sealed record Illegal(string Edge) : TransitionResult;

    /// <summary>No such work.</summary>
    public sealed record NotFound : TransitionResult;
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
/// The §20 durable coordination seam: every mutation is ONE transaction —
/// per-Work advisory xact lock → guard check → version-checked UPDATE →
/// work_events append → outbox append → NOTIFY → commit. Sinks/providers are
/// only ever called after commit, by the dispatcher.
/// </summary>
public interface ICoordinationStore
{
    /// <summary>Create a Work at <c>open</c> version 1 with its <c>created</c> timeline row and outbox progress event.</summary>
    Task<WorkView> CreateWorkAsync(string objective, Guid? projectId, string actor, CancellationToken ct);

    /// <summary>Guard-checked, version-fenced state transition.</summary>
    Task<TransitionResult> ApplyTransitionAsync(Guid workId, WorkState to, long expectedVersion, string actor, string? reason, CancellationToken ct);

    /// <summary>Version-fenced steer; bumps the work version and appends a steer timeline row + outbox progress event.</summary>
    Task<SteerResult> ApplySteerAsync(Guid workId, string directive, string commandId, long expectedVersion, CancellationToken ct);

    /// <summary>Current projection; null when absent.</summary>
    Task<WorkView?> GetAsync(Guid workId, CancellationToken ct);

    /// <summary>Timeline rows ascending by (created_at, event_id).</summary>
    Task<IReadOnlyList<WorkEventRow>> TimelineAsync(Guid workId, CancellationToken ct);
}
