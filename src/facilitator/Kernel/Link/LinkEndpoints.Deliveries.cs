using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>Long-poll delivery claims + the receipt endpoint (parity with the hub via <see cref="LinkReceipts"/>).</summary>
public static partial class LinkEndpoints
{
    private static void MapDeliveries(RouteGroupBuilder group) =>
        group.MapGet("/deliveries", async (
            HttpRequest request,
            LinkService sessions,
            LinkLedger ledger,
            LinkConnectionRegistry registry,
            LinkDeliveryPump pump,
            LinkOptions options,
            CancellationToken ct) =>
        {
            var principal = LinkAuthMiddleware.RequirePrincipal(request.HttpContext);
            if (wait(request) < 0 || limit(request) is null)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("wait must be 0..60 and limit 1..64"));
            }

            // Locked rule: a delivery read REQUIRES a live session (the hub
            // handshake is the session bootstrap; this call refreshes the
            // lease implicitly).
            var session = await sessions.TryGetLiveAsync(principal.EndpointId, ct);
            if (session is null)
            {
                return Results.Problem(ProblemErrors.SessionNotFound(principal.EndpointId));
            }

            // Every delivery read refreshes the lease (S:314): long-poll IS
            // the heartbeat for a socketless principal, so a polling adapter
            // never loses its session to the sweeper (§7.3 lease = truth).
            await sessions.TouchSessionAsync(principal.EndpointId, session.SessionEpoch, options.HeartbeatSeconds, ct);
            var claimed = await ClaimWindowAsync(principal.EndpointId, session.SessionEpoch, ledger, pump, registry, options, request, ct);
            if (claimed.Count == 0 && wait(request) > 0)
            {
                await registry.WaitForWakeAsync(
                    principal.EndpointId,
                    TimeSpan.FromSeconds(wait(request)),
                    async () =>
                    {
                        await pump.PumpEndpointAsync(principal.EndpointId, options.MaxInFlight, ct);
                        return await ledger.HasClaimableAsync(principal.EndpointId, ct);
                    },
                    ct);
                claimed = await ClaimWindowAsync(principal.EndpointId, session.SessionEpoch, ledger, pump, registry, options, request, ct);
            }

            return Results.Ok(claimed.Select(c => c.Delivery).ToArray());
        });

    private static void MapReceipt(RouteGroupBuilder group) =>
        group.MapPost("/deliveries/{deliveryId:guid}/receipt", async (
            Guid deliveryId,
            HttpRequest request,
            LinkReceipts receipts,
            ControlFrameValidator frames,
            CancellationToken ct) =>
        {
            var principal = LinkAuthMiddleware.RequirePrincipal(request.HttpContext);
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            JsonElement frame;
            try
            {
                using var doc = JsonDocument.Parse(body.Text.Length == 0 ? "{}" : body.Text);
                // Identity travels in the path (OpenAPI ReceiptWithoutDeliveryId).
                var merged = doc.RootElement.Deserialize<Dictionary<string, JsonElement>>(Web)!;
                merged["deliveryId"] = JsonDocument.Parse($"\"{deliveryId:D}\"").RootElement.Clone();
                frame = JsonSerializer.SerializeToElement(merged, Web);
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            var verdict = frames.ValidateReceipt(frame);
            if (!verdict.Ok)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(verdict.Message ?? "receipt violates receipt.v1.json"));
            }

            // One commit path with the hub: ownership + idempotent ack + pump + wake.
            if (!await receipts.CommitAsync(principal, deliveryId, frame.GetRawText(), ct))
            {
                return Results.Problem(ProblemErrors.DeliveryNotFound(deliveryId));
            }

            return Results.NoContent();
        });

    private static async Task<IReadOnlyList<ClaimedDelivery>> ClaimWindowAsync(
        string endpointId,
        long epoch,
        LinkLedger ledger,
        LinkDeliveryPump pump,
        LinkConnectionRegistry registry,
        LinkOptions options,
        HttpRequest request,
        CancellationToken ct)
    {
        await pump.PumpEndpointAsync(endpointId, options.MaxInFlight, ct);
        var max = limit(request) ?? options.MaxInFlight;
        return await ledger.ClaimAsync(endpointId, epoch, max, options.MaxInFlight, ct);
    }

    private static int wait(HttpRequest request) =>
        request.Query.TryGetValue("wait", out var w) && int.TryParse(w, out var parsed) ? Math.Clamp(parsed, 0, 60) : 0;

    private static int? limit(HttpRequest request)
    {
        if (!request.Query.TryGetValue("limit", out var l))
        {
            return 64;
        }

        return int.TryParse(l, out var parsed) && parsed is >= 1 and <= 64 ? parsed : null;
    }
}
