using System.Data.Common;
using System.Text.Json;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>A claimed delivery ready to be offered to the endpoint.</summary>
/// <param name="Delivery">Wire payload (deliveryId + envelope + deliveredAt).</param>
/// <param name="SessionEpoch">Epoch the claim charged the delivery to.</param>
public sealed record ClaimedDelivery(DeliveryDto Delivery, long SessionEpoch);

/// <summary>
/// The durable delivery/receipt ledger (<c>link_deliveries</c>). Delivery identity = outbox id, so a receipt for an
/// already-acked delivery answers the LATEST attempt and the
/// dropped-receipt → redelivery → duplicate loop terminates.
/// The pump owns every transition except the receipt ack, which flips the
/// matching outbox row in the SAME transaction (the dispatcher never
/// double-charges a link row).
/// </summary>
public sealed class LinkLedger(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    LinkOptions options,
    TimeProvider timeProvider,
    KernelMetrics metrics)
{
    /// <summary>
    /// Materialize link-addressed outbox rows into the ledger while a live
    /// session exists (insert-or-ignore = idempotent). Selecting
    /// <c>status NOT IN (delivered, dead_letter)</c> covers rows the dispatcher
    /// holds <c>leased</c> to the LinkSink, so both views converge on the
    /// ledger as the single delivery truth. Never mutates the outbox row.
    /// </summary>
    public async Task<int> MaterializeAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO facilitator.link_deliveries
                (delivery_id, endpoint_id, session_epoch, state, delivered_at, acked_at, receipt, attempts, redeliver_after)
            SELECT o.outbox_id, substring(o.recipient_endpoint_id from 6), s.session_epoch, 'pending', NULL, NULL, NULL, 0, NULL
              FROM facilitator.outbox o
              JOIN facilitator.link_sessions s ON s.logical_endpoint_id = substring(o.recipient_endpoint_id from 6)
             WHERE o.recipient_endpoint_id LIKE 'link.%'
               AND o.status NOT IN ('delivered', 'dead_letter')
            ON CONFLICT (delivery_id) DO NOTHING
            """, ct);
        return rows;
    }

    /// <summary>Materialize only for one endpoint (used after Hello / inline before a claim).</summary>
    public async Task<int> MaterializeAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO facilitator.link_deliveries
                (delivery_id, endpoint_id, session_epoch, state, delivered_at, acked_at, receipt, attempts, redeliver_after)
            SELECT o.outbox_id, substring(o.recipient_endpoint_id from 6), s.session_epoch, 'pending', NULL, NULL, NULL, 0, NULL
              FROM facilitator.outbox o
              JOIN facilitator.link_sessions s ON s.logical_endpoint_id = substring(o.recipient_endpoint_id from 6)
             WHERE o.recipient_endpoint_id LIKE 'link.%'
               AND substring(o.recipient_endpoint_id from 6) = {endpointId}
               AND o.status NOT IN ('delivered', 'dead_letter')
            ON CONFLICT (delivery_id) DO NOTHING
            """, ct);
    }

    /// <summary>
    /// Claim up to <c>min(limit, maxInFlight − inFlight)</c> claimable rows
    /// (fresh pending or in-flight past their redelivery deadline) and move
    /// them in_flight under the caller's epoch. <c>FOR UPDATE SKIP LOCKED</c>
    /// makes concurrent claims (hub pump + HTTP GET) disjoint.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        string endpointId, long sessionEpoch, int limit, int maxInFlight, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var window = Math.Min(limit, maxInFlight - await CountInFlightAsync(db, endpointId, now, ct));
        var rows = new List<ClaimedDelivery>();
        if (window <= 0)
        {
            return rows;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = """
                WITH claim AS (
                  SELECT delivery_id
                    FROM facilitator.link_deliveries
                   WHERE endpoint_id = @endpoint
                     AND (state = 'pending' OR (state = 'in_flight' AND redeliver_after < @now))
                   ORDER BY delivered_at NULLS LAST, delivery_id
                   LIMIT @window
                   FOR UPDATE SKIP LOCKED
                )
                UPDATE facilitator.link_deliveries d
                   SET state = 'in_flight',
                       session_epoch = @epoch,
                       delivered_at = @now,
                       redeliver_after = @now + make_interval(secs => @lease),
                       attempts = d.attempts + 1
                  FROM claim, facilitator.outbox o
                 WHERE d.delivery_id = claim.delivery_id
                   AND o.outbox_id = claim.delivery_id
                RETURNING d.delivery_id, o.envelope, d.delivered_at, d.session_epoch
                """;
            Add(command, "@endpoint", endpointId);
            Add(command, "@now", now);
            Add(command, "@window", window);
            Add(command, "@epoch", sessionEpoch);
            Add(command, "@lease", options.InFlightLeaseSeconds);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ClaimedDelivery(
                    new DeliveryDto(
                        reader.GetGuid(0),
                        JsonDocument.Parse(reader.GetString(1)).RootElement.Clone(),
                        reader.GetDateTime(2)),
                    reader.GetInt64(3)));
            }
        }

        await tx.CommitAsync(ct);
        foreach (var _ in rows)
        {
            metrics.DeliveryAttempt(); // reuse facilitator.delivery.attempts for link claims
        }

        return rows;
    }


    /// <summary>Count a session's in-flight deliveries (window arithmetic + FlowControl).</summary>
    public async Task<int> CountInFlightAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await CountInFlightAsync(db, endpointId, timeProvider.GetUtcNow().UtcDateTime, ct);
    }

    private static async Task<int> CountInFlightAsync(FacilitatorDbContext db, string endpointId, DateTime now, CancellationToken ct)
    {
        // Raw command on a fresh context: the pooled connection may be closed
        // (no EF query ran on it), so open when needed — but never when a tx
        // already owns it (Npgsql Open throws on an open connection).
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM facilitator.link_deliveries
             WHERE endpoint_id = @endpoint AND state = 'in_flight' AND redeliver_after >= @now
            """;
        Add(command, "@endpoint", endpointId);
        Add(command, "@now", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Pending (not yet in-flight) count for the wake probe.</summary>
    public async Task<bool> HasClaimableAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.LinkDeliveries.AsNoTracking().AnyAsync(
            d => d.EndpointId == endpointId
              && (d.State == "pending" || (d.State == "in_flight" && d.RedeliverAfter < now)), ct);
    }

    /// <summary>Envelope snapshot of one ledger row (receipt-echo / diagnostics).</summary>
    public async Task<JsonElement?> GetEnvelopeAsync(Guid deliveryId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var text = await db.Outbox.AsNoTracking()
            .Where(o => o.OutboxId == deliveryId)
            .Select(o => o.Envelope)
            .FirstOrDefaultAsync(ct);
        return text is null ? null : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Current ledger state ('pending'|'in_flight'|'acked') or null when unknown.</summary>
    public async Task<(string State, string EndpointId)?> StateAsync(Guid deliveryId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkDeliveries.AsNoTracking()
            .Where(d => d.DeliveryId == deliveryId)
            .Select(d => new { d.State, d.EndpointId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.State, row.EndpointId);
    }

    /// <summary>Receipt-rejection budget; mirrors <c>DbEventDispatcher.MaxAttempts</c>.
    /// After this many offers a rejected delivery is terminally recorded like
    /// any other receipt, so a poison delivery still drains.</summary>
    public const int MaxReceiptAttempts = 5;

    /// <summary>
    /// Record a receipt. Terminal receipts (<c>accepted</c>, <c>duplicate</c>,
    /// and rejections with a permanent reason: policy, no-binding, unsupported)
    /// flip the ledger row to acked AND the matching outbox row to delivered in
    /// ONE transaction so the dispatcher never double-charges. A RETRYABLE rejection — <c>child-busy</c> / <c>child-disconnected</c>
    /// / <c>child-offline</c>, the transient-capacity reasons the gateway
    /// documents as "honest, retryable upstream" — is NOT terminal: the receipt
    /// jsonb is recorded, the row stays in-flight behind a short redelivery
    /// backoff (which also charges the window, i.e. it applies flow control),
    /// and only after <see cref="MaxReceiptAttempts"/> offers does it settle.
    /// Terminally acking a busy rejection would silently lose the command
    /// (a steer comment rejected child-busy once never landed at all).
    /// </summary>
    public async Task<bool> AckAsync(Guid deliveryId, string endpointId, string receiptJson, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Close the materialization race inside the tx (an endpoint can only
        // receipt a delivery it was offered, and being offered implies the
        // ledger row; the insert makes the read-then-ack at the endpoint
        // atomic against a concurrent pump).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO facilitator.link_deliveries
                 (delivery_id, endpoint_id, session_epoch, state, delivered_at, acked_at, receipt, attempts, redeliver_after)
             VALUES
                 ({deliveryId}, {endpointId}, 0, 'pending', NULL, NULL, NULL, 0, NULL)
             ON CONFLICT (delivery_id) DO NOTHING
             """, ct);

        var requeue = IsRetryableRejection(receiptJson)
            && await db.LinkDeliveries.AsNoTracking().Where(d => d.DeliveryId == deliveryId)
                   .Select(d => d.Attempts).FirstOrDefaultAsync(ct) < MaxReceiptAttempts;

        var rows = requeue
            ? await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.link_deliveries
                    SET state = 'in_flight', receipt = {receiptJson}::jsonb,
                        redeliver_after = {now} + make_interval(secs => {options.RedeliverBackoffSeconds})
                  WHERE delivery_id = {deliveryId} AND endpoint_id = {endpointId}
                 """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.link_deliveries
                    SET state = 'acked', acked_at = COALESCE(acked_at, {now}), receipt = {receiptJson}::jsonb
                  WHERE delivery_id = {deliveryId} AND endpoint_id = {endpointId}
                 """, ct);
        if (rows == 0)
        {
            return false;
        }

        if (!requeue)
        {
            await OutboxRepository.MarkDeliveredOnAsync(db, deliveryId, now, ct);
            // Execution-attempt law: an accepted assign receipt IS the
            // proof of execution start — open the attempt row (execution id =
            // delivery id) and walk the assignment to running, in this tx.
            await Facilitator.Kernel.Work.ExecutionLedger.OpenAttemptOnAckAsync(db, deliveryId, now, ct);
            // Vanished address (F5): a permanent rejection naming an unmappable
            // conversation retires the notification's address (obsolete + a
            // timeline row), in the same tx as the receipt — the item endures.
            await Facilitator.Kernel.Notifications.NotificationLedger
                .ApplyObsoleteOnRejectAsync(db, deliveryId, receiptJson, now, ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>True for a <c>rejected</c> receipt whose reason names a
    /// transient endpoint condition (child quota full / child link down) as
    /// opposed to a permanent refusal (policy, no-binding, unknown child).</summary>
    internal static bool IsRetryableRejection(string receiptJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(receiptJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.GetProperty("status").GetString() != "rejected")
            {
                return false;
            }

            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            return reason.StartsWith("child-busy", StringComparison.Ordinal)
                || reason.StartsWith("child-disconnected", StringComparison.Ordinal)
                || reason.StartsWith("child-offline", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
