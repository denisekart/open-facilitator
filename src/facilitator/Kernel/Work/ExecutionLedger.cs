using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>Outcome of a finalize (fence + terminal settle) attempt.</summary>
public enum FinalizeOutcome
{
    /// <summary>Assignment finalized (or already terminal for this outcome) and gated siblings woken.</summary>
    Settled,

    /// <summary>No assignment row for the event's assignmentId — a connector-synthetic
    /// identity or a legacy inline path; accepted as a no-op (today's behavior).</summary>
    NoAssignment,

    /// <summary>Terminal replay against an already-settled assignment; accepted, nothing written.</summary>
    Duplicate,

    /// <summary>Fence: the event's generation is not the assignment's current
    /// generation. The event is REFUSED (422) and the manifest/ledger stay untouched.</summary>
    Fenced,
}

/// <summary>The finalize result: outcome plus, for <see cref="FinalizeOutcome.Fenced"/>,
/// the typed problem the ingress answers with.</summary>
public sealed record FinalizeResult(FinalizeOutcome Outcome, ProblemDetails? Problem = null);

/// <summary>
/// The execution-attempt ledger law: an accepted
/// <c>work.assign</c> receipt opens the attempt row (execution id = the
/// delivery id — the link ledger's own identity for the execution) and walks
/// the assignment <c>routed→accepted→running</c> through legal edges; a
/// terminal <c>completed</c>/<c>failed</c> event runs the fence (the event's
/// stamped <c>executionGeneration</c> must equal the assignment's
/// <c>current_execution_generation</c>, else a 422 <c>stale-generation</c>
/// refusal — a late completion from a fenced-over generation never settles
/// the assignment), walks the assignment to its terminal state, settles the
/// attempt row at the matching generation, and applies the wake law (one
/// UPDATE pulls the Work's gated routing intents forward so a DAG sibling
/// dispatches now, not at backoff). Every write is a status-guarded
/// single-row UPDATE: a racing ingress (event-before-receipt, replay, or a
/// concurrent replica) can only land on a legal edge or no-op.
/// </summary>
public static class ExecutionLedger
{
    /// <summary>Attempt statuses with no execution behind them any more
    /// (public: the session-loss law scopes its sweep to the complement).</summary>
    public static readonly string[] AttemptTerminal = ["stopped", "failed", "lost"];

    /// <summary>Assignment states where a late accepted receipt must not open anything.</summary>
    private static readonly string[] AssignmentTerminal = ["completed", "failed", "cancelled", "expired"];

    /// <summary>Postgres regex for a canonical/N-format uuid: the wire
    /// assignmentId is a free-form id-token, so the ledger join casts it to
    /// uuid only when it is shaped like one (a connector-synthetic id then
    /// joins to nothing, no-op, exactly like a missing row).</summary>
    private const string UuidShape =
        "^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$";

    /// <summary>The accepted-receipt hook, inside the ack transaction
    /// (<see cref="LinkLedger.AckAsync"/>): an <c>accepted</c> receipt for a
    /// delivered <c>work.assign</c> row opens the attempt
    /// (<c>execution_id</c> = the delivery id, generation from the envelope)
    /// and advances the assignment <c>routed→accepted→running</c>. Guarded
    /// end to end: a non-assign or non-accepted delivery, a connector-synthetic
    /// (no-row) assignment id, or an assignment already finalized by a racing
    /// completed event all land as no-ops. One guarded statement per legal
    /// edge — a subset applies, never an illegal jump.</summary>
    public static async Task OpenAttemptOnAckAsync(
        FacilitatorDbContext db, Guid deliveryId, DateTime now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO facilitator.execution_attempts
                     (execution_id, assignment_id, generation, status, started_at, ended_at)
             SELECT d.delivery_id,
                    a.assignment_id,
                    COALESCE((o.envelope->'data'->>'executionGeneration')::int, 1),
                    'working', {now}, NULL
               FROM facilitator.link_deliveries d
               JOIN facilitator.outbox o ON o.outbox_id = d.delivery_id
               JOIN facilitator.assignments a ON a.assignment_id = (CASE WHEN o.envelope->'data'->>'assignmentId' ~* {UuidShape}
                    THEN (o.envelope->'data'->>'assignmentId')::uuid END)
              WHERE d.delivery_id = {deliveryId}
                AND o.event_type = 'org.facilitator.command.work.assign.v1'
                AND d.receipt->>'status' = 'accepted'
                AND a.status <> ALL ({AssignmentTerminal}::text[])
             ON CONFLICT (execution_id) DO NOTHING
             """, ct);

        // routed→accepted then accepted→running: the legal two-step, each edge
        // guarded. An event-raced finalize may already sit on a later state —
        // the guard then no-ops both statements.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.assignments SET status = 'accepted', updated_at = {now}
              WHERE status = 'routed'
                AND assignment_id = (
                    SELECT a.assignment_id
                      FROM facilitator.link_deliveries d
                      JOIN facilitator.outbox o ON o.outbox_id = d.delivery_id
                      JOIN facilitator.assignments a ON a.assignment_id = (CASE WHEN o.envelope->'data'->>'assignmentId' ~* {UuidShape}
                           THEN (o.envelope->'data'->>'assignmentId')::uuid END)
                     WHERE d.delivery_id = {deliveryId}
                       AND o.event_type = 'org.facilitator.command.work.assign.v1')
             """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.assignments SET status = 'running', updated_at = {now}
              WHERE status = 'accepted'
                AND assignment_id = (SELECT assignment_id FROM facilitator.execution_attempts WHERE execution_id = {deliveryId})
             """, ct);
    }

    /// <summary>The terminal-event arm (fence + finalize + wake law).
    /// <paramref name="outcome"/> is <c>completed</c> or <c>failed</c>. A
    /// no-assignment-row event (connector op, legacy inline) is
    /// <see cref="FinalizeOutcome.NoAssignment"/> — accepted, untouched.</summary>
    public static async Task<FinalizeResult> FinalizeAsync(
        FacilitatorDbContext db, Guid workId, Guid assignmentId, int executionGeneration, string outcome, DateTime now, CancellationToken ct)
    {
        var row = await db.Assignments.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .Select(a => new { a.AssignmentId, a.Status, a.CurrentExecutionGeneration })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return new FinalizeResult(FinalizeOutcome.NoAssignment);
        }

        // The fence: only the assignment's CURRENT generation may finalize.
        if (row.CurrentExecutionGeneration != executionGeneration)
        {
            return new FinalizeResult(
                FinalizeOutcome.Fenced,
                ProblemErrors.StaleGeneration(executionGeneration, row.CurrentExecutionGeneration));
        }

        var to = outcome == "completed" ? "completed" : "failed";
        if (Array.IndexOf(AssignmentTerminal, row.Status) >= 0)
        {
            // Already terminal at this generation: a replay (or a competing
            // terminal outcome — first writer won). Accepted, no re-write.
            return new FinalizeResult(FinalizeOutcome.Duplicate);
        }

        // Walk the legal chain to the terminal edge one guarded UPDATE per
        // edge (routed→accepted→running→terminal; input_required is a live
        // side-state, not a step toward terminal).
        await WalkToRunningAsync(db, assignmentId, now, ct);
        var edge = row.Status is "running" or "input_required" ? (row.Status, to) : ("running", to);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.assignments SET status = {to}, updated_at = {now}
              WHERE assignment_id = {assignmentId} AND status = {edge.Item1}
             """, ct);

        // Settle the attempt row at the matching generation (none exists when
        // the terminal event raced its own accepted receipt — the
        // OpenAttemptOnAckAsync guard then keeps the row closed out never re-opened).
        // The attempt vocabulary has no 'completed': a normal end is 'stopped'.
        var attemptTo = outcome == "completed" ? "stopped" : "failed";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.execution_attempts
                SET status = {attemptTo}, ended_at = {now}
              WHERE assignment_id = {assignmentId}
                AND generation = {executionGeneration}
                AND status <> ALL ({AttemptTerminal}::text[])
             """, ct);

        // The wake law: a terminal sibling pulls the work's still-gated
        // routing intents forward so the funnel re-evaluates NOW.
        await DependencyGate.WakeGatedRoutingIntentsAsync(db, workId, now, ct);
        return new FinalizeResult(FinalizeOutcome.Settled);
    }

    /// <summary>Advance an assignment to <c>running</c> through guarded legal
    /// edges (routed→accepted→running). Idempotent; a row already running
    /// (or input_required) is untouched.</summary>
    internal static async Task WalkToRunningAsync(
        FacilitatorDbContext db, Guid assignmentId, DateTime now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.assignments SET status = 'accepted', updated_at = {now}
              WHERE assignment_id = {assignmentId} AND status = 'routed'
             """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.assignments SET status = 'running', updated_at = {now}
              WHERE assignment_id = {assignmentId} AND status = 'accepted'
             """, ct);
    }

    /// <summary>The assignmentId an assign/command envelope carries (the
    /// ledger join the nudge sweeper uses), or null.</summary>
    public static Guid? AssignmentIdOf(string envelopeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelopeJson);
            return doc.RootElement.GetProperty("data").TryGetProperty("assignmentId", out var v)
                && v.ValueKind == JsonValueKind.String
                && Guid.TryParse(v.GetString(), out var id)
                    ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The endpoint that carried this assignment (latest assign or
    /// redispatch row's recipient), the same ownership read the steer arm
    /// uses: the work's assign-family rows come out of SQL, the envelope's
    /// assignmentId is matched in memory (jsonb has no translatable LIKE).
    /// Shared by the nudge sweeper and the session-loss law — the outbox IS
    /// the ownership ledger.</summary>
    public static async Task<string?> OwningEndpointAsync(FacilitatorDbContext db, Guid workId, Guid assignmentId, CancellationToken ct)
    {
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.WorkId == workId
                && o.EventType == "org.facilitator.command.work.assign.v1"
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Envelope, o.RecipientEndpointId })
            .ToListAsync(ct);
        // Same read-back as the steer arm: parse the envelope, do not text-match
        // it (jsonb output normalizes whitespace).
        var needle = assignmentId.ToString("N");
        var row = rows.FirstOrDefault(r => HasAssignmentId(r.Envelope, needle))?.RecipientEndpointId;
        return row?.StartsWith("link.", StringComparison.Ordinal) == true ? row["link.".Length..] : row;
    }

    /// <summary>True when an assign-family envelope's data carries exactly
    /// this assignment id (the ownership ledger read-back).</summary>
    public static bool HasAssignmentId(string envelope, string assignmentIdN)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("assignmentId", out var id)
                && id.GetString() == assignmentIdN;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
