using System.Collections.Concurrent;
using System.Text.Json;

namespace Facilitator.Kernel.Shared.Dispatch;

/// <summary>Delivery view of an outbox row, handed to sinks outside any tx.</summary>
/// <param name="OutboxId">Outbox row identity (delivery id).</param>
/// <param name="RecipientEndpointId">Logical channel address.</param>
/// <param name="EventType">CloudEvent type.</param>
/// <param name="CommandId">Business idempotency key.</param>
/// <param name="Envelope">Parsed CloudEvent envelope document.</param>
/// <param name="Attempts">Attempts charged to this delivery.</param>
public sealed record OutboxRow(Guid OutboxId, string RecipientEndpointId, string EventType, string CommandId, JsonElement Envelope, int Attempts);

/// <summary>Transport for one logical channel; the dispatcher resolves sinks by <see cref="Channel"/>.</summary>
public interface ISink
{
    /// <summary>Channel prefix (text before the first '.' of recipient_endpoint_id).</summary>
    string Channel { get; }

    /// <summary>Deliver one row; true = delivered (marks processed), false/throw = requeue.</summary>
    Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct);
}

/// <summary>One recorded successful delivery.</summary>
/// <param name="DeliveryId">The outbox row id.</param>
/// <param name="Attempts">Attempts consumed by the successful delivery.</param>
/// <param name="DeliveredAt">Delivery time (UTC).</param>
public sealed record Delivery(Guid DeliveryId, int Attempts, DateTime DeliveredAt);

/// <summary>
/// In-memory ledger of successful stub-sink deliveries (restart/dedup proof
/// surface). An assertion aid, not state: bounded at 10 000 entries, evicting
/// an arbitrary key on overflow.
/// </summary>
public sealed class DeliveryLedger
{
    private const int Capacity = 10_000;
    private readonly ConcurrentDictionary<Guid, Delivery> _deliveries = new();

    /// <summary>Record a successful delivery (last write wins per outbox id).</summary>
    public void Record(Delivery delivery)
    {
        if (_deliveries.Count >= Capacity && !_deliveries.ContainsKey(delivery.DeliveryId))
        {
            foreach (var key in _deliveries.Keys)
            {
                _deliveries.TryRemove(key, out _);
                break;
            }
        }

        _deliveries[delivery.DeliveryId] = delivery;
    }

    /// <summary>All recorded deliveries.</summary>
    public IReadOnlyList<Delivery> Snapshot() => _deliveries.Values.OrderBy(d => d.DeliveredAt).ToArray();
}

/// <summary>
/// Stub sink for channel <c>kernel</c> (Plan 02 target <c>kernel.log</c>):
/// appends to <see cref="DeliveryLedger"/> and logs. Replaced by real
/// transports (link server, providers) in later plans.
/// </summary>
public sealed class KernelLogSink(DeliveryLedger ledger, ILogger<KernelLogSink> logger, TimeProvider timeProvider) : ISink
{
    /// <inheritdoc />
    public string Channel => "kernel";

    /// <inheritdoc />
    public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
    {
        ledger.Record(new Delivery(row.OutboxId, row.Attempts, timeProvider.GetUtcNow().UtcDateTime));
        logger.LogInformation(
            "delivered outbox row {OutboxId} type={EventType} command={CommandId} attempts={Attempts}",
            row.OutboxId, row.EventType, row.CommandId, row.Attempts);
        return Task.FromResult(true);
    }
}
