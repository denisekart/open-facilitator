using System.Text.Json;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Admin;

/// <summary>GET /api/admin/summary roll-up (every section table-derived).</summary>
public sealed record AdminSummary(
    int Teams,
    int Projects,
    Dictionary<string, int> WorkByState,
    Dictionary<string, int> AssignmentsByState,
    Dictionary<string, int> ExecutionsByState,
    int GatewaysOnline,
    int GatewaysOffline,
    Dictionary<string, int> InboxByState,
    Dictionary<string, int> OutboxByState,
    int DeadLettersTotal,
    DateTime? OldestPendingOutboxAt,
    Dictionary<string, int> ApprovalsByState,
    int ArtifactsTotal,
    Dictionary<string, int> ManifestsByResultState,
    Dictionary<string, int> MemoryByStatus,
    IReadOnlyList<string> TeamSlugs,
    int LinkSessionsLive);

/// <summary>One universal-search hit row (identity + a display line + state).</summary>
public sealed record AdminSearchHit(string Id, string Display, string State, DateTime At);

/// <summary>GET /api/admin/search sections (each independently bounded).</summary>
public sealed record AdminSearchResults(
    string Query,
    IReadOnlyList<AdminSearchHit> Works,
    IReadOnlyList<AdminSearchHit> Projects,
    IReadOnlyList<AdminSearchHit> Assignments,
    IReadOnlyList<AdminSearchHit> Artifacts,
    IReadOnlyList<AdminSearchHit> Memories,
    IReadOnlyList<AdminSearchHit> Messages);

/// <summary>GET /api/admin/links row (trustMode displayed; lease = online authority).</summary>
public sealed record AdminLinkRow(
    string EndpointId,
    string Team,
    string TrustMode,
    string? CapabilitiesDigest,
    bool Online,
    long? SessionEpoch,
    DateTime? LeaseUntil,
    DateTime? LastHeartbeat,
    string? ReplicaId,
    int LiveSockets);

/// <summary>Assignment affected by an offline gateway (stale view).</summary>
public sealed record AdminAffectedAssignment(Guid AssignmentId, Guid WorkId, string Status);

/// <summary>GET /api/admin/links: gateway rows + affected assignments of offline teams.</summary>
public sealed record AdminLinksView(IReadOnlyList<AdminLinkRow> Gateways, IReadOnlyList<AdminAffectedAssignment> AffectedAssignments);

/// <summary>One kernel replica: live leases it stamps, of those the endpoints
/// it actually holds sockets for, fleet queue depth (every replica answers
/// the same number from the shared tables).</summary>
public sealed record AdminReplicaRow(string ReplicaId, int LiveSessions, int SocketsHere);

/// <summary>GET /api/admin/replicas: who answers, who leases, queue depth.</summary>
public sealed record AdminReplicasView(
    string AnsweringReplicaId,
    IReadOnlyList<AdminReplicaRow> Replicas,
    long OutboxPending);

/// <summary>
/// GET /api/admin/deliveries merged row: kernel-plane outbox rows, link-plane
/// delivery rows, and dead-letter rows (the superset that replaced
/// <c>/api/dev/deliveries</c> and <c>GET /api/dead-letters</c>).
/// </summary>
public sealed record AdminDeliveryRow(
    string Plane,
    Guid Id,
    string State,
    int Attempts,
    string? LastError,
    DateTime? AvailableAt,
    string? EndpointId,
    long? SessionEpoch,
    DateTime? RedeliverAfter,
    string? Reason,
    DateTime UpdatedAt);

/// <summary>GET /api/admin/metrics roll-up: SQL gauges + windowed table facts + process counters.</summary>
public sealed record AdminMetrics(
    long OutboxPending,
    long InboxPending,
    long DeadLetters,
    long LinkSessions,
    IReadOnlyList<AdminHourBucket> DeadLettersPerHour,
    IReadOnlyList<AdminHourBucket> AuditDeniesPerHour,
    IReadOnlyList<AdminHourBucket> ArtifactsStoredPerHour,
    IReadOnlyList<AdminHourBucket> ArtifactsRejectedPerHour,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>One UTC-hour bucket of a windowed counter.</summary>
public sealed record AdminHourBucket(DateTime Hour, long Count);

/// <summary>GET /api/admin/projects/{id} detail.</summary>
public sealed record AdminProjectDetail(
    Guid ProjectId,
    string Name,
    string? TeamSlug,
    JsonElement Config,
    DateTime CreatedAt,
    IReadOnlyList<AdminWorkRow> Works);

/// <summary>One work row inside a detail/list payload.</summary>
public sealed record AdminWorkRow(Guid WorkId, string Objective, string State, long Version, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>GET /api/admin/work/{id} detail: aggregate + assignments/attempts + approvals + manifests.</summary>
public sealed record AdminWorkDetail(
    AdminWorkRow Work,
    string? ProjectId,
    IReadOnlyList<AdminAssignmentRow> Assignments,
    IReadOnlyList<AdminApprovalRow> Approvals,
    IReadOnlyList<AdminManifestRow> Manifests);

/// <summary>Assignment row with its execution attempts. <c>DependsOnJson</c>
/// is the raw DAG gate (additive pass-through, the AllowedCandidates precedent);
/// <c>NudgeCount</c>/<c>Stalled</c> derive from the nudge ledger at the
/// assignment's current generation (stalled = the cap is spent).</summary>
public sealed record AdminAssignmentRow(
    Guid AssignmentId,
    string Status,
    int CurrentExecutionGeneration,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AdminAttemptRow> Attempts,
    string? DependsOnJson,
    int NudgeCount,
    bool Stalled);

/// <summary>Execution-attempt row (the workspace-execution proxy).</summary>
public sealed record AdminAttemptRow(Guid ExecutionId, int Generation, string Status, DateTime StartedAt, DateTime? EndedAt);

/// <summary>Approval row on the work-detail surface.</summary>
public sealed record AdminApprovalRow(Guid ApprovalId, string State, string ActionDigest, DateTime CreatedAt, DateTime? DecidedAt);

/// <summary>Manifest row (artifact refs) on the work-detail surface.</summary>
public sealed record AdminManifestRow(string ManifestId, int Revision, string? RevisionOf, string ResultState, string State, Guid ArtifactId, IReadOnlyList<string> OutputArtifactIds, DateTime RecordedAt);

/// <summary>One provisioning_operations ledger row (GET
/// /api/admin/provisioning): the launcher ops the scale-law sweep appended.</summary>
public sealed record AdminProvisioningRow(
    string OperationId,
    string WorkloadId,
    string Team,
    string Operation,
    string State,
    Guid? WorkId,
    Guid? AssignmentId,
    int? Generation,
    string? Reason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>One audit trail row (GET /api/admin/audit) — the gate and
/// feature audit writes, newest first, with the jsonb detail surfaced as a
/// raw element.</summary>
public sealed record AdminAuditRow(
    Guid AuditId,
    DateTime OccurredAt,
    string? Actor,
    string Action,
    string? Resource,
    string Decision,
    string? Reason,
    JsonElement Detail,
    string? TraceId);

/// <summary>
/// The admin read models: bounded <c>GroupBy</c> counts
/// and <c>Take</c>-limited projections over the existing ledgers — one
/// round-trip per section, the <c>EfCoordinationStore.TimelineAsync</c>
/// pattern. Stale gateways collapse into offline (the sweeper deletes expired
/// session rows); socket counts are best-effort per replica, the DB lease
/// stays the sole online authority.
/// </summary>
public sealed class AdminReadStores(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    LinkConnectionRegistry connections,
    KernelMetrics metrics,
    TimeProvider timeProvider)
{
    /// <summary>Full roll-up over every ledger section.</summary>
    public async Task<AdminSummary> SummaryAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var teams = await db.Teams.CountAsync(ct);
        var projects = await db.Projects.CountAsync(ct);
        var works = await GroupByAsync(db.Works.Select(w => w.State), ct);
        var assignments = await GroupByAsync(db.Assignments.Select(a => a.Status), ct);
        var executions = await GroupByAsync(db.ExecutionAttempts.Select(e => e.Status), ct);
        var inbox = await GroupByAsync(db.Inbox.Select(i => i.State), ct);
        var outbox = await GroupByAsync(db.Outbox.Select(o => o.Status), ct);
        var deadLetters = await db.DeadLetters.CountAsync(ct);
        var oldestPending = await db.Outbox.Where(o => o.Status == "pending").Select(o => (DateTime?)o.AvailableAt).MinAsync(ct);
        var approvals = await GroupByAsync(db.Approvals.Select(a => a.State), ct);
        var artifacts = await db.Artifacts.CountAsync(ct);
        var manifests = await GroupByAsync(db.ResultManifests.Select(m => m.ResultState), ct);
        var memory = await GroupByAsync(db.MemoryEntries.Select(m => m.Status), ct);
        var liveSessions = await db.LinkSessions.Where(s => s.LeaseUntil > now).CountAsync(ct);
        var teamSlugs = await db.Teams.OrderBy(t => t.Slug).Select(t => t.Slug).ToListAsync(ct);
        var gatewayEndpoints = await db.LinkEnrollments.Where(e => e.Role == "gateway").Select(e => e.EndpointId).ToListAsync(ct);
        var onlineEndpoints = await db.LinkSessions
            .Where(s => s.LeaseUntil > now && gatewayEndpoints.Contains(s.LogicalEndpointId))
            .Select(s => s.LogicalEndpointId)
            .ToListAsync(ct);
        return new AdminSummary(
            teams, projects, works, assignments, executions,
            onlineEndpoints.Count, gatewayEndpoints.Count - onlineEndpoints.Count,
            inbox, outbox, deadLetters, oldestPending, approvals, artifacts, manifests, memory, teamSlugs, liveSessions);
    }

    /// <summary>
    /// SQL-bounded universal search: typed sections, each
    /// <c>LIMIT</c>ed, <c>ILIKE</c> over text columns; a guid-shaped q also
    /// matches the message id columns by equality.
    /// </summary>
    public async Task<AdminSearchResults> SearchAsync(string query, string? type, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var take = Math.Clamp(limit, 1, 20);
        var pattern = $"%{query}%";
        var guid = Guid.TryParse(query, out var parsed) ? parsed : (Guid?)null;

        var works = Section(type, "works") ? await db.Works.AsNoTracking()
            .Where(w => EF.Functions.ILike(w.Objective, pattern))
            .OrderByDescending(w => w.UpdatedAt)
            .Take(take)
            .Select(w => new AdminSearchHit(w.WorkId.ToString(), w.Objective, w.State, w.UpdatedAt))
            .ToListAsync(ct) : [];
        var projects = Section(type, "projects") ? await db.Projects.AsNoTracking()
            .Where(p => EF.Functions.ILike(p.Name, pattern))
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .Select(p => new AdminSearchHit(p.ProjectId.ToString(), p.Name, "project", p.CreatedAt))
            .ToListAsync(ct) : [];
        var assignments = Section(type, "assignments") ? await db.Assignments.AsNoTracking()
            .Where(a => a.CapabilityId != null && EF.Functions.ILike(a.CapabilityId, pattern))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(take)
            .Select(a => new AdminSearchHit(a.AssignmentId.ToString(), a.CapabilityId!, a.Status, a.UpdatedAt))
            .ToListAsync(ct) : [];
        var artifacts = Section(type, "artifacts") ? await db.Artifacts.AsNoTracking()
            .Where(a => EF.Functions.ILike(a.MediaType, pattern) || EF.Functions.ILike(a.Digest, pattern))
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new AdminSearchHit(a.ArtifactId.ToString(), a.MediaType, a.State, a.CreatedAt))
            .ToListAsync(ct) : [];
        var memories = Section(type, "memories") ? await db.MemoryEntries.AsNoTracking()
            .Where(m => EF.Functions.ILike(m.Content, pattern))
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new AdminSearchHit(m.EntryId.ToString(), m.ContentSummary, m.Status, m.CreatedAt))
            .ToListAsync(ct) : [];
        var messages = Section(type, "messages") ? await MessageHitsAsync(db, pattern, guid, take, ct) : [];
        return new AdminSearchResults(query, works, projects, assignments, artifacts, memories, messages);
    }

    /// <summary>Project detail + newest works (bounded 50).</summary>
    public async Task<AdminProjectDetail?> ProjectAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => new { p.ProjectId, p.Name, TeamSlug = p.Team!.Slug, p.Config, p.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (project is null)
        {
            return null;
        }

        var works = await db.Works.AsNoTracking()
            .Where(w => w.ProjectId == projectId)
            .OrderByDescending(w => w.CreatedAt)
            .Take(50)
            .Select(w => new AdminWorkRow(w.WorkId, w.Objective, w.State, w.Version, w.CreatedAt, w.UpdatedAt))
            .ToListAsync(ct);
        return new AdminProjectDetail(project.ProjectId, project.Name, project.TeamSlug,
            JsonDocument.Parse(project.Config).RootElement.Clone(), project.CreatedAt, works);
    }

    /// <summary>Work detail: aggregate + assignments/attempts + approvals + manifests. Null = unknown work.</summary>
    public async Task<AdminWorkDetail?> WorkAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new { w.WorkId, w.Objective, w.State, w.Version, w.CreatedAt, w.UpdatedAt, ProjectId = w.ProjectId == null ? null : w.ProjectId.Value.ToString() })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return null;
        }

        var assignments = await db.Assignments.AsNoTracking()
            .Where(a => a.WorkId == workId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AdminAssignmentRow(
                a.AssignmentId,
                a.Status,
                a.CurrentExecutionGeneration,
                a.CreatedAt,
                a.UpdatedAt,
                db.ExecutionAttempts.Where(e => e.AssignmentId == a.AssignmentId)
                    .OrderBy(e => e.Generation)
                    .Select(e => new AdminAttemptRow(e.ExecutionId, e.Generation, e.Status, e.StartedAt, e.EndedAt))
                    .ToList(),
                a.DependsOn,
                // Doctrine: the budget is per stall episode, so both
                // fields read the LATEST episode's ledger row — progress that
                // opens a fresh episode clears the stalled flag.
                (int?)db.Nudges.Where(n => n.AssignmentId == a.AssignmentId
                        && n.Generation == a.CurrentExecutionGeneration
                        && n.StallEpisode == db.Nudges
                            .Where(x => x.AssignmentId == a.AssignmentId && x.Generation == a.CurrentExecutionGeneration)
                            .Max(x => x.StallEpisode))
                    .Select(n => (int?)n.NudgeCount).FirstOrDefault() ?? 0,
                db.Nudges.Any(n => n.AssignmentId == a.AssignmentId
                    && n.Generation == a.CurrentExecutionGeneration
                    && n.NudgeCount >= Work.NudgeSweeper.NudgeCap
                    && n.StallEpisode == db.Nudges
                        .Where(x => x.AssignmentId == a.AssignmentId && x.Generation == a.CurrentExecutionGeneration)
                        .Max(x => x.StallEpisode))))
            .ToListAsync(ct);
        var approvals = await db.Approvals.AsNoTracking()
            .Where(a => a.WorkId == workId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AdminApprovalRow(a.ApprovalId, a.State, a.ActionDigest, a.CreatedAt, a.DecidedAt))
            .ToListAsync(ct);
        var manifests = await db.ResultManifests.AsNoTracking()
            .Where(m => m.WorkId == workId)
            .OrderBy(m => m.RecordedAt)
            .Select(m => new AdminManifestRow(m.ManifestId, m.Revision, m.RevisionOf, m.ResultState, m.State, m.ArtifactId, m.OutputArtifactIds, m.RecordedAt))
            .ToListAsync(ct);
        return new AdminWorkDetail(
            new AdminWorkRow(work.WorkId, work.Objective, work.State, work.Version, work.CreatedAt, work.UpdatedAt),
            work.ProjectId,
            assignments,
            approvals,
            manifests);
    }

    /// <summary>Gateway link state (role=gateway enrollments, lease = online authority) + affected assignments.</summary>
    public async Task<AdminLinksView> LinksAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enrollments = await (from e in db.LinkEnrollments.AsNoTracking()
                                 join t in db.Teams.AsNoTracking() on e.TeamId equals t.TeamId into tj
                                 from t in tj.DefaultIfEmpty()
                                 where e.Role == "gateway"
                                 select new { e.EndpointId, TeamSlug = t == null ? string.Empty : t.Slug, e.TrustMode, e.CapabilitiesDigest })
            .ToListAsync(ct);
        var sessions = await db.LinkSessions.AsNoTracking()
            .Where(s => s.LeaseUntil > now)
            .ToDictionaryAsync(s => s.LogicalEndpointId, ct);

        var gateways = enrollments
            .Select(r =>
            {
                if (!sessions.TryGetValue(r.EndpointId, out var session))
                {
                    return new AdminLinkRow(r.EndpointId, r.TeamSlug, r.TrustMode, r.CapabilitiesDigest,
                        false, null, null, null, null, connections.ForEndpoint(r.EndpointId).Count);
                }

                return new AdminLinkRow(
                    r.EndpointId,
                    r.TeamSlug,
                    r.TrustMode,
                    r.CapabilitiesDigest,
                    true,
                    session.SessionEpoch,
                    session.LeaseUntil,
                    session.LastHeartbeat,
                    session.ReplicaId,
                    connections.ForEndpoint(r.EndpointId).Count);
            })
            .OrderBy(g => g.EndpointId, StringComparer.Ordinal)
            .ToList();

        var offlineTeams = gateways.Where(g => !g.Online && g.Team.Length > 0).Select(g => g.Team).ToHashSet(StringComparer.Ordinal);
        var affected = await db.Assignments.AsNoTracking()
            .Where(a => a.TeamId != null && db.Teams.Any(t => t.TeamId == a.TeamId && offlineTeams.Contains(t.Slug))
                && a.Status != "completed" && a.Status != "failed" && a.Status != "cancelled" && a.Status != "expired")
            .OrderBy(a => a.CreatedAt)
            .Take(100)
            .Select(a => new AdminAffectedAssignment(a.AssignmentId, a.WorkId, a.Status))
            .ToListAsync(ct);
        return new AdminLinksView(gateways, affected);
    }

    /// <summary>Merged durable delivery view (kernel outbox + link ledger + dead letters).</summary>
    public async Task<IReadOnlyList<AdminDeliveryRow>> DeliveriesAsync(string? state, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var take = Math.Clamp(limit, 1, 200);
        var items = new List<AdminDeliveryRow>();

        if (state is null or "pending" or "leased" or "delivered")
        {
            items.AddRange(await db.Outbox.AsNoTracking()
                .Where(o => state == null || o.Status == state)
                .OrderByDescending(o => o.CreatedAt)
                .Take(take)
                .Select(o => new AdminDeliveryRow("kernel", o.OutboxId, o.Status, o.Attempts, o.LastError, o.AvailableAt,
                    o.RecipientEndpointId, null, null, null, o.CreatedAt))
                .ToListAsync(ct));
        }

        if (state is null or "pending" or "in_flight" or "acked")
        {
            items.AddRange(await db.LinkDeliveries.AsNoTracking()
                .Where(d => state == null || d.State == state)
                .OrderByDescending(d => d.DeliveredAt ?? DateTime.MinValue)
                .Take(take)
                .Select(d => new AdminDeliveryRow("link", d.DeliveryId, d.State, d.Attempts, null, d.DeliveredAt,
                    d.EndpointId, d.SessionEpoch, d.RedeliverAfter, null, d.DeliveredAt ?? DateTime.MinValue))
                .ToListAsync(ct));
        }

        if (state is null or "dead_letter")
        {
            var dead = await db.DeadLetters.AsNoTracking()
                .OrderByDescending(d => d.CreatedAt)
                .Take(take)
                .ToListAsync(ct);
            var sourceIds = dead.Select(d => d.OutboxId).ToList();
            var sources = await db.Outbox.AsNoTracking()
                .Where(o => sourceIds.Contains(o.OutboxId))
                .ToDictionaryAsync(o => o.OutboxId, ct);
            items.AddRange(dead.Select(d =>
            {
                var src = sources.TryGetValue(d.OutboxId, out var row) ? row : null;
                return new AdminDeliveryRow("dead_letter", d.DeadLetterId, "dead_letter",
                    src?.Attempts ?? 0, src?.LastError, src?.AvailableAt, src?.RecipientEndpointId, null, null, d.Reason, d.CreatedAt);
            }));
        }

        return items.OrderByDescending(i => i.UpdatedAt).Take(take).ToList();
    }

    /// <summary>Newest provisioning ops: the scale-law ledger,
    /// newest-100, camelCase via the ASP.NET defaults.</summary>
    public async Task<IReadOnlyList<AdminProvisioningRow>> ProvisioningAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ProvisioningOps.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .Select(o => new AdminProvisioningRow(
                o.OperationId, o.WorkloadId, o.Team, o.Operation, o.State,
                o.WorkId, o.AssignmentId, o.Generation, o.Reason, o.CreatedAt, o.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// GET /api/admin/audit: the audit trail, newest first, filterable by
    /// action (exact), actor (exact), and a <c>since</c> floor. <c>limit</c>
    /// defaults to 50, capped at 200 — the chat history screen points here
    /// after AI-initiated executions.
    /// </summary>
    public async Task<IReadOnlyList<AdminAuditRow>> AuditAsync(string? action, string? actor, DateTime? since, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        var query = db.Audit.AsNoTracking();
        if (action is { Length: > 0 })
        {
            query = query.Where(a => a.Action == action);
        }

        if (actor is { Length: > 0 })
        {
            query = query.Where(a => a.Actor == actor);
        }

        if (since is { } floor)
        {
            query = query.Where(a => a.OccurredAt >= floor);
        }

        var rows = await query
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.AuditId)
            .Take(take)
            .Select(a => new { a.AuditId, a.OccurredAt, a.Actor, a.Action, a.Resource, a.Decision, a.Reason, a.Detail, a.TraceId })
            .ToListAsync(ct);
        return rows.Select(a => new AdminAuditRow(
            a.AuditId, a.OccurredAt, a.Actor, a.Action, a.Resource, a.Decision, a.Reason,
            JsonDocument.Parse(a.Detail).RootElement.Clone(), a.TraceId)).ToList();
    }

    /// <summary>Per-replica fleet view: live link-session leases
    /// grouped by the replica that stamps them, plus how many of those
    /// endpoints THIS replica actually holds a socket for (the honest
    /// answer-identity read: a stale ReplicaId on a lease means a dead
    /// replica's gateway, not a live twin).</summary>
    public async Task<AdminReplicasView> ReplicasAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await db.LinkSessions.AsNoTracking()
            .Where(s => s.LeaseUntil > now)
            .Select(s => new { s.ReplicaId, s.LogicalEndpointId })
            .ToListAsync(ct);
        var heldHere = connections.ForAllEndpoints();
        var rows = sessions
            .GroupBy(s => s.ReplicaId, StringComparer.Ordinal)
            .Select(g => new AdminReplicaRow(
                g.Key,
                g.Count(),
                g.Count(s => heldHere.Contains(s.LogicalEndpointId, StringComparer.Ordinal))))
            .OrderBy(r => r.ReplicaId, StringComparer.Ordinal)
            .ToList();
        return new AdminReplicasView(
            connections.ReplicaId,
            rows,
            await db.Outbox.CountAsync(o => o.Status == "pending", ct));
    }

    /// <summary>Inline metrics roll-up: SQL gauges + windowed per-hour table facts + process counter snapshot.</summary>
    public async Task<AdminMetrics> MetricsAsync(string? window, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var hours = window == "24h" ? 24 : 1;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since = now.AddHours(-hours);
        var outboxPending = await db.Outbox.CountAsync(o => o.Status == "pending", ct);
        var inboxPending = await db.Inbox.CountAsync(i => i.State == "pending", ct);
        var deadLetters = await db.DeadLetters.CountAsync(ct);
        var linkSessions = await db.LinkSessions.CountAsync(s => s.LeaseUntil > now, ct);
        return new AdminMetrics(
            outboxPending, inboxPending, deadLetters, linkSessions,
            await HourBucketsAsync(db.DeadLetters.Where(d => d.CreatedAt >= since).Select(d => d.CreatedAt), ct),
            await HourBucketsAsync(db.Audit.Where(a => a.Decision == "deny" && a.OccurredAt >= since).Select(a => a.OccurredAt), ct),
            await HourBucketsAsync(db.Artifacts.Where(a => a.StoredAt != null && a.StoredAt >= since).Select(a => a.StoredAt!.Value), ct),
            await HourBucketsAsync(db.Artifacts.Where(a => a.State == "rejected" && a.CreatedAt >= since).Select(a => a.CreatedAt), ct),
            metrics.CounterTotals());
    }

    private static async Task<Dictionary<string, int>> GroupByAsync(IQueryable<string> states, CancellationToken ct) =>
        (await states
            .GroupBy(s => s)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct))
        .ToDictionary(r => r.Key, r => r.Count);

    private static async Task<IReadOnlyList<AdminHourBucket>> HourBucketsAsync(IQueryable<DateTime> stamps, CancellationToken ct)
    {
        var raw = await stamps.ToListAsync(ct);
        return raw
            .GroupBy(t => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc))
            .Select(g => new AdminHourBucket(g.Key, g.LongCount()))
            .OrderBy(b => b.Hour)
            .ToList();
    }

    private static async Task<IReadOnlyList<AdminSearchHit>> MessageHitsAsync(
        FacilitatorDbContext db, string pattern, Guid? guid, int take, CancellationToken ct)
    {
        var hits = new List<AdminSearchHit>();
        if (guid is not null)
        {
            var g = guid.Value;
            hits.AddRange(await db.Outbox.AsNoTracking()
                .Where(o => o.OutboxId == g)
                .Select(o => new AdminSearchHit(o.OutboxId.ToString(), o.EventType, o.Status, o.CreatedAt))
                .ToListAsync(ct));
            hits.AddRange(await db.Inbox.AsNoTracking()
                .Where(i => i.InboxId == g)
                .Select(i => new AdminSearchHit(i.InboxId.ToString(), i.EventId, i.State, i.ReceivedAt))
                .ToListAsync(ct));
            hits.AddRange(await db.DeadLetters.AsNoTracking()
                .Where(d => d.DeadLetterId == g)
                .Select(d => new AdminSearchHit(d.DeadLetterId.ToString(), d.Reason, "dead_letter", d.CreatedAt))
                .ToListAsync(ct));
        }
        else
        {
            hits.AddRange(await db.Outbox.AsNoTracking()
                .Where(o => EF.Functions.ILike(o.CommandId, pattern))
                .OrderByDescending(o => o.CreatedAt)
                .Take(take)
                .Select(o => new AdminSearchHit(o.OutboxId.ToString(), o.CommandId, o.Status, o.CreatedAt))
                .ToListAsync(ct));
            hits.AddRange(await db.Inbox.AsNoTracking()
                .Where(i => EF.Functions.ILike(i.EventId, pattern))
                .OrderByDescending(i => i.ReceivedAt)
                .Take(take)
                .Select(i => new AdminSearchHit(i.InboxId.ToString(), i.EventId, i.State, i.ReceivedAt))
                .ToListAsync(ct));
        }

        return hits.Take(take).ToList();
    }

    private static bool Section(string? type, string name) => type is null || type == name;
}
