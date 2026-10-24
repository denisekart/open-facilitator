using System.Diagnostics;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Facilitator.Kernel.Dispatch;

/// <summary>
/// §20 dispatcher surface: claim + deliver one batch, and reclaim expired
/// leases. The <see cref="OutboxDispatcher"/> hosted service is a thin timer
/// over these; tests drive them directly (restart/crash simulation).
/// </summary>
public interface IEventDispatcher
{
    /// <summary>Claim up to 20 due rows and deliver them; returns delivered count.</summary>
    Task<int> DrainOnceAsync(CancellationToken ct);

    /// <summary>Flip expired leases back to pending; returns reclaimed count.</summary>
    Task<int> ReclaimExpiredLeasesAsync(CancellationToken ct);
}

/// <summary>
/// Postgres-backed outbox dispatcher. Claim runs <c>FOR UPDATE SKIP LOCKED</c>
/// inside a short tx (expired leases are claimable by the same predicate);
/// sinks are called OUTSIDE any tx (spec §19.3). Failures requeue with capped
/// exponential backoff; five charged attempts dead-letter the row with the
/// original envelope snapshot (appendix §5.2/§5.6). Every outbox-state SET
/// runs through <see cref="OutboxRepository"/>.
/// </summary>
public sealed class DbEventDispatcher : IEventDispatcher
{
    private const int MaxAttempts = 5;
    private const int BatchSize = 20;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GaugeCache = TimeSpan.FromSeconds(5);

    private readonly string _claimSql = $"""
        WITH claim AS (
          SELECT outbox_id
            FROM facilitator.outbox
           WHERE (status = 'pending' AND available_at <= now())
              OR (status = 'leased' AND leased_until < now())
           ORDER BY available_at
           LIMIT {BatchSize}
           FOR UPDATE SKIP LOCKED
        )
        UPDATE facilitator.outbox o
           SET status = 'leased',
               lease_owner = @owner,
               leased_until = now() + interval '{(int)LeaseDuration.TotalSeconds} seconds',
               attempts = o.attempts + 1
          FROM claim
         WHERE o.outbox_id = claim.outbox_id
        RETURNING o.outbox_id, o.recipient_endpoint_id, o.event_type, o.command_id, o.envelope, o.attempts
        """;

    private readonly IDbContextFactory<Persistence.FacilitatorDbContext> _contextFactory;
    private readonly OutboxRepository _outbox;
    private readonly IReadOnlyDictionary<string, ISink> _sinks;
    private readonly KernelMetrics _metrics;
    private readonly ILogger<DbEventDispatcher> _logger;
    private readonly string _replicaId;
    private long _lastGaugeRefreshTicks = -1;

    /// <summary>Create the dispatcher.</summary>
    public DbEventDispatcher(
        IDbContextFactory<Persistence.FacilitatorDbContext> contextFactory,
        OutboxRepository outbox,
        IEnumerable<ISink> sinks,
        KernelMetrics metrics,
        IConfiguration configuration,
        ILogger<DbEventDispatcher> logger)
    {
        _contextFactory = contextFactory;
        _outbox = outbox;
        // Last registration per channel wins so tests can replace a sink.
        _sinks = sinks.GroupBy(s => s.Channel, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        _metrics = metrics;
        _logger = logger;
        _replicaId = Facilitator.Kernel.Shared.KernelOptions.ReplicaId(configuration);
    }

    /// <inheritdoc />
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await RefreshGaugesAsync(ct);

        var claimed = await ClaimAsync(ct);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var delivered = 0;
        foreach (var row in claimed)
        {
            using var span = KernelActivity.Source.StartActivity("outbox.dispatch", ActivityKind.Internal);
            span?.SetTag("facilitator.delivery_id", row.OutboxId);
            span?.SetTag("facilitator.channel", ChannelOf(row.RecipientEndpointId));
            _metrics.DeliveryAttempt();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!_sinks.TryGetValue(ChannelOf(row.RecipientEndpointId), out var sink))
                {
                    // No route (e.g. link.* until Plan 03 session routing):
                    // requeue with backoff, never dead-letter (locked decision).
                    _metrics.DeliveryLatency(stopwatch.Elapsed.TotalMilliseconds);
                    await _outbox.RequeueAsync(row.OutboxId, $"no sink registered for channel '{ChannelOf(row.RecipientEndpointId)}'", ct);
                    continue;
                }

                var ok = await sink.DeliverAsync(row, ct);
                _metrics.DeliveryLatency(stopwatch.Elapsed.TotalMilliseconds);
                if (ok)
                {
                    await _outbox.MarkDeliveredAsync(row.OutboxId, ct);
                    delivered++;
                    continue;
                }

                await HandleFailureAsync(row, "sink refused the delivery", noRoute: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics.DeliveryLatency(stopwatch.Elapsed.TotalMilliseconds);
                await HandleFailureAsync(row, ex.Message, noRoute: false, ct);
            }
        }

        return delivered;
    }

    /// <inheritdoc />
    public Task<int> ReclaimExpiredLeasesAsync(CancellationToken ct) => _outbox.ReclaimExpiredLeasesAsync(ct);

    private static string ChannelOf(string recipientEndpointId)
    {
        var dot = recipientEndpointId.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? recipientEndpointId : recipientEndpointId[..dot];
    }

    private async Task<List<OutboxRow>> ClaimAsync(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();

        var rows = new List<OutboxRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = _claimSql;
            var owner = command.CreateParameter();
            owner.ParameterName = "@owner";
            owner.Value = _replicaId;
            command.Parameters.Add(owner);

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

    private async Task HandleFailureAsync(OutboxRow row, string error, bool noRoute, CancellationToken ct)
    {
        // "No route" (link.* before Plan 03 routing exists) requeues forever
        // with backoff — never dead-letters (locked decision).
        if (row.Attempts >= MaxAttempts && !noRoute)
        {
            await _outbox.DeadLetterAsync(row.OutboxId, error, row.Envelope, ct);
            _logger.LogWarning("dead-lettered outbox row {OutboxId} after {Attempts} attempts: {Error}", row.OutboxId, row.Attempts, error);
            return;
        }

        await _outbox.RequeueAsync(row.OutboxId, error, ct);
    }

    private async Task RefreshGaugesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var last = Interlocked.Read(ref _lastGaugeRefreshTicks);
        if (last >= 0 && now - new DateTime(last, DateTimeKind.Utc) < GaugeCache)
        {
            return;
        }

        Interlocked.Exchange(ref _lastGaugeRefreshTicks, now.Ticks);
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var outboxDepth = await db.Outbox.CountAsync(o => o.Status == "pending", ct);
        var inboxDepth = await db.Inbox.CountAsync(i => i.State == "pending", ct);
        var deadLetters = await db.DeadLetters.CountAsync(ct);
        _metrics.SetGauges(outboxDepth, inboxDepth, deadLetters);
    }
}
