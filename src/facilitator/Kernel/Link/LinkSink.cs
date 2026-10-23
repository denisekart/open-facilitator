using Facilitator.Kernel.Dispatch;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Routing liveness probe for channel <c>link</c>: the dispatcher's contract
/// is only "is there anywhere this can go". A live session means the pump owns
/// the row via the DB ledger (materialize + claim + push, with redelivery);
/// no session anywhere ⇒ false ⇒ Plan 02's no-route requeue-with-backoff
/// applies unchanged. Ordering/redelivery/attempts for link rows live in
/// <c>link_deliveries</c>, not the dispatcher.
/// </summary>
public sealed class LinkSink(LinkService sessions, LinkLedger ledger) : ISink
{
    /// <inheritdoc />
    public string Channel => "link";

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
    {
        var dot = row.RecipientEndpointId.IndexOf('.', StringComparison.Ordinal);
        var endpointId = dot < 0 ? row.RecipientEndpointId : row.RecipientEndpointId[(dot + 1)..];
        // Materialize the row into the ledger BEFORE answering "live": the
        // dispatcher flips the outbox row to 'delivered' on this true, and
        // materialization ignores delivered rows — skipping this inline step
        // would let the dispatcher's mark race the pump past the delivery.
        await ledger.MaterializeAsync(endpointId, ct);
        return await sessions.TryGetLiveAsync(endpointId, ct) is not null;
    }
}
