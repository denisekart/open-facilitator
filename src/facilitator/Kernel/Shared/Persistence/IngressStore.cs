using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>
/// Write-path seam for the ingress endpoints: inbox dedupe insert (ON CONFLICT
/// DO NOTHING) and the raw-SQL queries the endpoints expose.
/// </summary>
public sealed class IngressStore(IDbContextFactory<FacilitatorDbContext> contextFactory, TimeProvider timeProvider)
{
    private const string InsertInboxSql = """
        INSERT INTO facilitator.inbox AS i
            (inbox_id, event_source, event_id, command_id, provider, provider_delivery_id,
             payload, payload_sha256, work_id, state, available_at, attempt, received_at, processed_at)
        VALUES
            (@id, @source, @eventId, @commandId, NULL, NULL,
             @payload::jsonb, @sha, @workId, @state, @availableAt, @attempt, @receivedAt, @processedAt)
        ON CONFLICT DO NOTHING
        RETURNING i.inbox_id
        """;

    /// <summary>
    /// Insert an inbox row unless the (event_source,event_id) or command_id
    /// unique already exists. Returns (inboxId, duplicate).
    /// </summary>
    public async Task<(Guid InboxId, bool Duplicate)> InsertInboxAsync(
        Guid inboxId,
        string eventSource,
        string eventId,
        string? commandId,
        string payloadJson,
        Guid? workId,
        string state,
        DateTime? processedAt,
        CancellationToken ct)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = InsertInboxSql;
        Add(command, "@id", inboxId);
        Add(command, "@source", eventSource);
        Add(command, "@eventId", eventId);
        Add(command, "@commandId", (object?)commandId ?? DBNull.Value);
        Add(command, "@payload", payloadJson);
        Add(command, "@sha", sha);
        Add(command, "@workId", (object?)workId ?? DBNull.Value);
        Add(command, "@state", state);
        Add(command, "@availableAt", now);
        Add(command, "@attempt", processedAt is null ? 0 : 1);
        Add(command, "@receivedAt", now);
        Add(command, "@processedAt", (object?)processedAt ?? DBNull.Value);
        var connection = command.Connection ?? throw new InvalidOperationException("command has no connection");
        await connection.OpenAsync(ct);
        var inserted = await command.ExecuteScalarAsync(ct);
        if (inserted is Guid insertedId)
        {
            return (insertedId, false);
        }

        await using var probe = await contextFactory.CreateDbContextAsync(ct);
        var existing = await probe.Inbox.AsNoTracking()
            .Where(i => (i.EventSource == eventSource && i.EventId == eventId)
                     || (commandId != null && i.CommandId == commandId))
            .Select(i => new { i.InboxId })
            .FirstOrDefaultAsync(ct);
        return (existing?.InboxId ?? inboxId, true);
    }

    /// <summary>
    /// Settle an inbox row: <c>processed</c> on success, <c>failed</c> when an
    /// error is recorded — dedupe-on-success.
    /// </summary>
    public async Task MarkProcessedAsync(Guid inboxId, string? error, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.inbox
                SET state = {(error is null ? "processed" : "failed")}, processed_at = {now}, error = {error}
              WHERE inbox_id = {inboxId}
             """, ct);
    }

    /// <summary>Append a timeline row without touching the version fence (audit notes).</summary>
    public async Task AppendTimelineNoteAsync(Guid workId, string kind, string actor, string detailJson, string? correlationId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var version = await db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = kind,
            Actor = actor,
            Detail = detailJson,
            CorrelationId = correlationId,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Newest 50 works, newest first; when <paramref name="teamSlugs"/> is non-null, only works of those teams.</summary>
    public async Task<IReadOnlyList<WorkView>> ListRecentAsync(
        IReadOnlyList<string>? teamSlugs, CancellationToken ct) =>
        await ListRecentAsync(teamSlugs, state: null, team: null, limit: 50, offset: 0, ct);

    /// <summary>The queue-shaped list (D2/N28): filterable by state and one
    /// team, paged (limit capped at 200), newest first. <c>team</c> narrows
    /// within the visible set — a team outside visibility still answers empty,
    /// never another team's works.</summary>
    public async Task<IReadOnlyList<WorkView>> ListRecentAsync(
        IReadOnlyList<string>? teamSlugs, string? state, string? team, int limit, int offset, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var query = db.Works.AsNoTracking();
        if (teamSlugs is not null)
        {
            query = query.Where(w => (w.Team != null && teamSlugs.Contains(w.Team.Slug))
                || (w.Team == null && w.Project != null && teamSlugs.Contains(w.Project.Team!.Slug)));
        }

        if (state is { Length: > 0 })
        {
            query = query.Where(w => w.State == state);
        }

        if (team is { Length: > 0 })
        {
            query = query.Where(w => (w.Team != null && w.Team.Slug == team)
                || (w.Team == null && w.Project != null && w.Project.Team!.Slug == team));
        }

        return await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit <= 0 ? 50 : limit, 1, 200))
            .Select(WorkProjection.View)
            .ToListAsync(ct);
    }

    /// <summary>Open routing intents with age (D2: "waiting long" is a query,
    /// not a guess): pending outbox rows on the routing channel, oldest first.</summary>
    public async Task<IReadOnlyList<PendingIntentRow>> PendingIntentsAsync(
        IReadOnlyList<string>? teamSlugs, string? team, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var query =
            from o in db.Outbox.AsNoTracking()
            join w in db.Works.AsNoTracking() on o.WorkId equals (Guid?)w.WorkId
            where o.Status == "pending" && o.RecipientEndpointId == Facilitator.Kernel.Work.RoutingIntent.Recipient
                  && o.WorkId != null
            select new { Row = o, Work = w };
        if (teamSlugs is not null)
        {
            query = query.Where(x => (x.Work.Team != null && teamSlugs.Contains(x.Work.Team.Slug))
                || (x.Work.Team == null && x.Work.Project != null && teamSlugs.Contains(x.Work.Project.Team!.Slug)));
        }

        if (team is { Length: > 0 })
        {
            query = query.Where(x => (x.Work.Team != null && x.Work.Team.Slug == team)
                || (x.Work.Team == null && x.Work.Project != null && x.Work.Project.Team!.Slug == team));
        }

        return await query
            .OrderBy(x => x.Row.AvailableAt)
            .Select(x => new PendingIntentRow(
                x.Row.OutboxId, x.Row.CommandId, x.Row.Envelope, x.Row.Attempts, x.Row.AvailableAt, x.Row.CreatedAt))
            .Take(500)
            .ToListAsync(ct);
    }

    /// <summary>The oldest live routing-intent age in seconds (0 when nothing
    /// waits) — the starvation gauge (I5/N28).</summary>
    public async Task<double> OldestPendingIntentAgeSecondsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var oldest = await db.Outbox.AsNoTracking()
            .Where(o => o.Status == "pending" && o.RecipientEndpointId == Facilitator.Kernel.Work.RoutingIntent.Recipient)
            .Select(o => (DateTime?)o.CreatedAt)
            .MinAsync(ct);
        return oldest is { } at ? Math.Max(0, (timeProvider.GetUtcNow().UtcDateTime - at).TotalSeconds) : 0;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>One pending routing intent as the queue read renders it.</summary>
public sealed record PendingIntentRow(
    Guid OutboxId, string CommandId, string Envelope, int Attempts, DateTime AvailableAt, DateTime CreatedAt);
