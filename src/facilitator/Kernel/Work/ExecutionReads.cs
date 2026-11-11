using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>One assignment line as the work read renders it (F1): an operator
/// looking at a work sees where it sits, how often it tried, and whether the
/// endpoint that took it is alive — without joining <c>/api/admin/links</c>.</summary>
public sealed record AssignmentLine(
    Guid AssignmentId,
    string? CapabilityId,
    string Status,
    int Generation,
    int Attempts,
    string? EndpointId,
    string? LastError,
    bool? EndpointOnline,
    string? QueueReason,
    DateTime UpdatedAt);

/// <summary>GET /api/works/{id}/executions (F1): assignment lines with liveness.
/// Liveness reuses the link-session lease — the same truth <c>/api/admin/links</c>
/// renders; no new state. The endpoint a line dispatches to is read from its
/// own <c>assignment.transition</c> timeline rows (the funnel writes them), so
/// this read adds nothing the timeline does not already record.</summary>
public static class ExecutionReads
{
    /// <summary>Advisory queue reason a gateway status report may set (D2/P13:
    /// capacity is observed, never owned); the read renders it distinctly from
    /// <c>running</c> — it is never a Work/Assignment state.</summary>
    public const string WaitingForCapacity = "waiting-for-capacity";

    /// <summary>The assignment lines of one work (newest-touched first).</summary>
    public static async Task<List<AssignmentLine>> LinesAsync(
        IDbContextFactory<FacilitatorDbContext> contextFactory, Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var lines = await db.Assignments.AsNoTracking()
            .Where(a => a.WorkId == workId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);
        var ids = lines.Select(a => a.AssignmentId).ToArray();
        var attempts = await db.ExecutionAttempts.AsNoTracking()
            .Where(e => ids.Contains(e.AssignmentId))
            .Select(e => new { e.AssignmentId, e.Generation, e.Status })
            .ToListAsync(ct);
        var transitions = await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == workId
                && (e.Kind == "assignment.transition" || e.Kind == "assignment.queue-reason"))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Detail)
            .ToListAsync(ct);
        var liveEndpoints = (await db.LinkSessions.AsNoTracking()
            .Where(s => s.LeaseUntil > now)
            .Select(s => s.LogicalEndpointId)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        return lines
            .Select(line =>
            {
                var key = line.AssignmentId.ToString();
                string? endpoint = null;
                string? lastError = null;
                string? queueReason = null;
                foreach (var detail in transitions)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(detail);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("assignmentId", out var id) || id.GetString() != key)
                        {
                            continue;
                        }

                        if (root.TryGetProperty("endpointId", out var ep) && ep.ValueKind == JsonValueKind.String)
                        {
                            endpoint = ep.GetString();
                        }

                        if (root.TryGetProperty("to", out var to) && to.GetString() is "failed" or "expired")
                        {
                            lastError = root.TryGetProperty("reason", out var reason)
                                ? reason.GetString()
                                : to.GetString();
                        }

                        // D2/P13: a gateway status report's advisory reason is
                        // rendered distinctly (observed, never a state).
                        if (root.TryGetProperty("queueReason", out var qr) && qr.ValueKind == JsonValueKind.String)
                        {
                            queueReason = qr.GetString() is { Length: > 0 } reason_ ? reason_ : null;
                        }
                    }
                    catch (JsonException)
                    {
                        // An unparseable timeline row names nothing.
                    }
                }

                var lineAttempts = attempts.Where(e => e.AssignmentId == line.AssignmentId).ToArray();
                return new AssignmentLine(
                    line.AssignmentId,
                    line.CapabilityId,
                    line.Status,
                    lineAttempts.Length == 0 ? 0 : lineAttempts.Max(e => e.Generation),
                    lineAttempts.Length,
                    endpoint,
                    lastError,
                    endpoint is { Length: > 0 } address ? liveEndpoints.Contains(address) : null,
                    queueReason,
                    line.UpdatedAt);
            })
            .ToList();
    }
}
