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

    /// <summary>Newest 50 works, newest first.</summary>
    public async Task<IReadOnlyList<WorkView>> ListRecentAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Works.AsNoTracking()
            .OrderByDescending(w => w.CreatedAt)
            .Take(50)
            .Select(w => new WorkView(w.WorkId, w.ProjectId, w.Objective, w.State, w.Version, w.CreatedAt, w.UpdatedAt))
            .ToListAsync(ct);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
