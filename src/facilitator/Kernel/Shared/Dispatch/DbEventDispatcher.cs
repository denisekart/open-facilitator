using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Dispatch;

/// <summary>
/// Dispatcher surface: claim + deliver one batch, and reclaim expired
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
/// Postgres-backed outbox dispatcher. The claim runs <c>FOR UPDATE SKIP LOCKED</c>
/// inside a short tx (expired leases are claimable by the same predicate) and
/// lives in <see cref="OutboxRepository"/>; sinks are called OUTSIDE any tx
/// Failures requeue with capped exponential backoff; five charged
/// attempts dead-letter the row with the original envelope snapshot.
/// Every outbox-state SET runs through <see cref="OutboxRepository"/>.
/// </summary>
public sealed class DbEventDispatcher : IEventDispatcher
{
    private const int MaxAttempts = 5;
    private const int DefaultBackoffCapSeconds = 300;
    private const string RetryPolicyResource = "retry-policy";
    private static readonly TimeSpan PolicyCache = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GaugeCache = TimeSpan.FromSeconds(5);
    private (int MaxAttempts, int BackoffCapSeconds)? _retryPolicy;
    private DateTime _lastRetryPolicyRead = DateTime.MinValue;

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
        _replicaId = KernelOptions.ReplicaId(configuration);
    }

    /// <inheritdoc />
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await RefreshGaugesAsync(ct);

        var claimed = await _outbox.ClaimAsync(_replicaId, ct);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var delivered = 0;
        foreach (var claimedRow in claimed)
        {
            var row = claimedRow;
            // Durable-queue trace law: a queue hop links explicitly. The
            // parent is extracted from the stored envelope's W3C traceparent
            // carrier (extract-or-fresh); ambient Activity.Current is never
            // captured implicitly across the durable boundary.
            var parent = default(ActivityContext);
            string? incoming = null;
            if (row.Envelope.ValueKind == JsonValueKind.Object
                && row.Envelope.TryGetProperty("traceparent", out var tp)
                && tp.ValueKind == JsonValueKind.String)
            {
                incoming = tp.GetString();
                ActivityContext.TryParse(incoming, null, isRemote: true, out parent);
            }

            using var span = KernelActivity.Source.StartActivity("outbox.dispatch", ActivityKind.Internal, parent);
            span?.SetTag("facilitator.delivery_id", row.OutboxId);
            span?.SetTag("facilitator.channel", ChannelOf(row.RecipientEndpointId));
            _metrics.DeliveryAttempt();

            // Fresh-side law: when the envelope carried no traceparent, the
            // freshly started dispatch trace is stamped into it and PERSISTED
            // before delivery, so retry/restart replays re-extract the same
            // trace (the extract side above) and the worker's derived events
            // are causally linked to the facilitator. Link recipients only;
            // envelopes that arrived traced keep their carrier untouched.
            if (incoming is null && Activity.Current is { } current && ChannelOf(row.RecipientEndpointId) == "link")
            {
                var stamped = JsonNode.Parse(row.Envelope.GetRawText())!.AsObject();
                stamped["traceparent"] = current.Id;
                var stampedText = stamped.ToJsonString();
                await _outbox.RestampEnvelopeAsync(row.OutboxId, _replicaId, stampedText, ct);
                row = row with { Envelope = JsonDocument.Parse(stampedText).RootElement.Clone() };
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!_sinks.TryGetValue(ChannelOf(row.RecipientEndpointId), out var sink))
                {
                    // No route (e.g. link.* with no session routing):
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
            catch (RoutePendingException pending)
            {
                // The funnel's no-route law: requeue with backoff, no DLQ
                // budget consumed — the candidate may simply not have booted
                // yet, and the routing-decision row carries the reason.
                _metrics.DeliveryLatency(stopwatch.Elapsed.TotalMilliseconds);
                await _outbox.RequeueAsync(row.OutboxId, pending.Reason, ct);
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

    private async Task HandleFailureAsync(OutboxRow row, string error, bool noRoute, CancellationToken ct)
    {
        var policy = await RetryPolicyAsync(ct);
        // "No route" (link.* with no live session) requeues forever
        // with backoff — never dead-letters (locked decision).
        if (row.Attempts >= policy.MaxAttempts && !noRoute)
        {
            await _outbox.DeadLetterAsync(row.OutboxId, error, row.Envelope, ct);
            _logger.LogWarning("dead-lettered outbox row {OutboxId} after {Attempts} attempts: {Error}", row.OutboxId, row.Attempts, error);
            return;
        }

        await _outbox.RequeueAsync(row.OutboxId, error, policy.BackoffCapSeconds, ct);
    }

    /// <summary>
    /// The stored <c>retry-policy</c> row (maxAttempts / backoffCapSeconds),
    /// cached 5 s; an absent or malformed row is the standing default (five
    /// charged attempts, a 300 s cap). This is that resource's only reader: no
    /// other behavior moves when an operator edits it.
    /// </summary>
    private async Task<(int MaxAttempts, int BackoffCapSeconds)> RetryPolicyAsync(CancellationToken ct)
    {
        if (_retryPolicy is { } cached && DateTime.UtcNow - _lastRetryPolicyRead < PolicyCache)
        {
            return cached;
        }

        var fallback = (MaxAttempts, DefaultBackoffCapSeconds);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(ct);
            var payload = await db.AdminConfig.AsNoTracking()
                .Where(c => c.Resource == RetryPolicyResource)
                .Select(c => c.Payload)
                .FirstOrDefaultAsync(ct);
            if (payload is not null)
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var attempts = root.TryGetProperty("maxAttempts", out var a) && a.TryGetInt32(out var parsedAttempts)
                    ? Math.Clamp(parsedAttempts, 1, 20)
                    : MaxAttempts;
                var cap = root.TryGetProperty("backoffCapSeconds", out var b) && b.TryGetInt32(out var parsedCap)
                    ? Math.Clamp(parsedCap, 1, 3600)
                    : DefaultBackoffCapSeconds;
                fallback = (attempts, cap);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A policy read never blocks delivery: the default stands.
        }

        _retryPolicy = fallback;
        _lastRetryPolicyRead = DateTime.UtcNow;
        return fallback;
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
