using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Persistence;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The ONE receipt-commit path (parity is the design goal): ownership check →
/// ledger ack → pump pass → long-poll wake. Both the hub <c>Receipt</c>
/// method and <c>POST /deliveries/&#123;id&#125;/receipt</c> call it, so the two
/// transports behave identically by construction.
/// </summary>
public sealed class LinkReceipts(
    LinkLedger ledger,
    LinkDeliveryPump pump,
    LinkConnectionRegistry registry,
    LinkOptions options)
{
    /// <summary>
    /// Commit a receipt: false when the delivery is unknown or owned by
    /// another endpoint (both transports answer not-found). An already-acked
    /// delivery is idempotent (204 again; the receipt jsonb is overwritten —
    /// the ledger is truth) and a 'duplicate' receipt terminates redelivery
    /// just like 'accepted'.
    /// </summary>
    public async Task<bool> CommitAsync(LinkPrincipal principal, Guid deliveryId, string receiptJson, CancellationToken ct)
    {
        var state = await ledger.StateAsync(deliveryId, ct);
        if (state is null || state.Value.EndpointId != principal.EndpointId)
        {
            return false;
        }

        if (!await ledger.AckAsync(deliveryId, principal.EndpointId, receiptJson, ct))
        {
            return false;
        }

        // Acking frees in-flight capacity ⇒ the waiting half of the pump + a wake for long-pollers.
        await pump.PumpEndpointAsync(principal.EndpointId, options.MaxInFlight, ct);
        registry.Wake(principal.EndpointId);
        return true;
    }
}
