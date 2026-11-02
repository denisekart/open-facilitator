using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>One dead-letter row for admin visibility (raw columns, DTO mapping at the edge).</summary>
/// <param name="DeadLetterId">Dead-letter row identity.</param>
/// <param name="OutboxId">Dead-lettered outbox row.</param>
/// <param name="Reason">Why the row dead-lettered.</param>
/// <param name="Envelope">Original CloudEvent snapshot (jsonb text).</param>
/// <param name="CreatedAt">Dead-letter time (UTC).</param>
public sealed record DeadLetterRow(Guid DeadLetterId, Guid OutboxId, string Reason, string Envelope, DateTime CreatedAt);

/// <summary>
/// Every <c>facilitator.outbox</c> state SET in one place: the SKIP LOCKED
/// claim, claim-expired reclaim, mark-delivered, requeue-with-backoff,
/// dead-letter, and the dev test-affordance insert (+wake). Raw SQL only where
/// the stance allows it (claim, lease reclaim, backoff arithmetic); EF for the
/// plain inserts. The admin replay re-enqueues a dead-lettered
/// envelope VERBATIM under a deterministic <c>replay:&lt;deadLetterId&gt;</c>
/// command id, so double-replay collides on <c>outbox_command_id_key</c> and
/// the ledger arbitrates idempotency — never the UI.
/// </summary>
public sealed class OutboxRepository(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider,
    Ofac.Protocol.Validation.EnvelopeValidator envelopeValidator,
    Facilitator.Kernel.Shared.Identity.AuditWriter audit)
{
    private const int ClaimBatchSize = 20;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

    private static readonly string ClaimSql = $"""
        WITH claim AS (
          SELECT outbox_id
            FROM facilitator.outbox
           WHERE (status = 'pending' AND available_at <= now())
              OR (status = 'leased' AND leased_until < now())
           ORDER BY available_at
           LIMIT {ClaimBatchSize}
           FOR UPDATE SKIP LOCKED
        )
        UPDATE facilitator.outbox o
           SET status = 'leased',
               lease_owner = @owner,
               leased_until = now() + interval '{(int)ClaimLease.TotalSeconds} seconds',
               attempts = o.attempts + 1
          FROM claim
         WHERE o.outbox_id = claim.outbox_id
        RETURNING o.outbox_id, o.recipient_endpoint_id, o.event_type, o.command_id, o.envelope, o.attempts
        """;

    /// <summary>Claim one batch of due rows under <c>FOR UPDATE SKIP LOCKED</c> and lease them to <paramref name="owner"/>.</summary>
    public async Task<List<OutboxRow>> ClaimAsync(string owner, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();

        var rows = new List<OutboxRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = ClaimSql;
            var p = command.CreateParameter();
            p.ParameterName = "@owner";
            p.Value = owner;
            command.Parameters.Add(p);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new OutboxRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
                    reader.GetInt32(5)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>Flip a delivered row out of the claim set (dispatcher success path).</summary>
    public async Task MarkDeliveredAsync(Guid outboxId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await MarkDeliveredOnAsync(db, outboxId, timeProvider.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>
    /// Persist a trace-stamped envelope for a row this replica currently
    /// leases (dispatcher's durable-queue extract-or-fresh law). Lease-guarded
    /// so a row reclaimed by another replica is never clobbered; envelope is
    /// the only column touched.
    /// </summary>
    public async Task RestampEnvelopeAsync(Guid outboxId, string owner, string envelopeJson, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.outbox
                SET envelope = {envelopeJson}::jsonb
              WHERE outbox_id = {outboxId} AND status = 'leased' AND lease_owner = {owner}
             """, ct);
    }

    /// <summary>Mark-delivered on a caller-owned context (participates in its tx — the link ack).</summary>
    public static async Task MarkDeliveredOnAsync(FacilitatorDbContext db, Guid outboxId, DateTime now, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.outbox
                SET status = 'delivered', processed_at = {now}, lease_owner = NULL, leased_until = NULL, last_error = NULL
              WHERE outbox_id = {outboxId}
             """, ct);

    /// <summary>Requeue a failed row with capped exponential backoff (charged attempts stay).</summary>
    public async Task RequeueAsync(Guid outboxId, string error, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.outbox
                SET status = 'pending',
                    available_at = now() + make_interval(secs => LEAST(power(2, attempts) * 2, 300)),
                    last_error = {error},
                    lease_owner = NULL,
                    leased_until = NULL
              WHERE outbox_id = {outboxId}
             """, ct);
    }

    /// <summary>Dead-letter a row: snapshot + status flip in ONE transaction.</summary>
    public async Task DeadLetterAsync(Guid outboxId, string error, JsonElement envelope, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.DeadLetters.Add(new DeadLetterEntity
        {
            DeadLetterId = Guid.NewGuid(),
            OutboxId = outboxId,
            Reason = error,
            Envelope = envelope.GetRawText(),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.outbox
                SET status = 'dead_letter', last_error = {error}, lease_owner = NULL, leased_until = NULL
              WHERE outbox_id = {outboxId}
             """, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Flip expired leases back to pending; returns reclaimed count.</summary>
    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE facilitator.outbox
               SET status = 'pending', lease_owner = NULL, leased_until = NULL
             WHERE status = 'leased' AND leased_until < now()
            """, ct);
    }

    /// <summary>Dev/test affordance: insert one outbox row and send the wake.</summary>
    public async Task<Guid> AppendAsync(string recipientEndpointId, string eventType, string commandId, string envelopeJson, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = recipientEndpointId,
            AggregateType = "work",
            EventType = eventType,
            CommandId = commandId,
            Envelope = envelopeJson,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        };
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.Outbox.Add(row);
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        return row.OutboxId;
    }

    /// <summary>Newest dead letters (admin visibility, appendix §5.6).</summary>
    public async Task<IReadOnlyList<DeadLetterRow>> ListDeadLettersAsync(int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.DeadLetters.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .Select(d => new DeadLetterRow(d.DeadLetterId, d.OutboxId, d.Reason, d.Envelope, d.CreatedAt))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>Outcome of an admin dead-letter replay.</summary>
    public enum ReplayOutcome
    {
        /// <summary>A fresh pending row landed (wake sent).</summary>
        Replayed,

        /// <summary>The deterministic replay command id exists — already replayed.</summary>
        AlreadyReplayed,

        /// <summary>No such dead-letter row.</summary>
        NotFound,

        /// <summary>The snapshot envelope fails the contract validator (fail-closed, no row).</summary>
        EnvelopeInvalid,
    }

    /// <summary>
    /// Re-enqueue a dead-lettered envelope verbatim: validate fail-closed,
    /// insert a NEW outbox row (fresh id, deterministic
    /// <c>replay:&lt;deadLetterId&gt;</c> command id) + audit row in one tx,
    /// then wake the dispatchers. The source row stays <c>dead_letter</c> as
    /// the diagnostic trail.
    /// </summary>
    public async Task<ReplayOutcome> ReplayAsync(Guid deadLetterId, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var dead = await db.DeadLetters.AsNoTracking().FirstOrDefaultAsync(d => d.DeadLetterId == deadLetterId, ct);
        if (dead is null)
        {
            return ReplayOutcome.NotFound;
        }

        var source = await db.Outbox.AsNoTracking().FirstOrDefaultAsync(o => o.OutboxId == dead.OutboxId, ct);
        var verdict = envelopeValidator.Validate(dead.Envelope);
        if (!verdict.Ok)
        {
            return ReplayOutcome.EnvelopeInvalid;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = source?.RecipientEndpointId ?? $"kernel.replay.{deadLetterId:N}",
            AggregateType = source?.AggregateType ?? "work",
            AggregateId = source?.AggregateId,
            EventType = source?.EventType ?? ExtractType(dead.Envelope),
            CommandId = $"replay:{deadLetterId}",
            Envelope = dead.Envelope,
            WorkId = source?.WorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
        audit.Append(db, new Facilitator.Kernel.Shared.Identity.AuditRecord(
            actor, "admin.dead-letter.replay", $"/api/admin/dead-letters/{deadLetterId}", "allow", null, dead.OutboxId.ToString(), null,
            System.Text.Json.JsonSerializer.Serialize(new { deadLetterId, sourceOutboxId = dead.OutboxId, reason = dead.Reason })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ReplayOutcome.AlreadyReplayed;
        }

        await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        return ReplayOutcome.Replayed;
    }

    private static string ExtractType(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()!
            : "org.facilitator.event.v1";
    }
}
