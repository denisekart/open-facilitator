using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Integrations;

/// <summary>
/// The provider webhook sink: <c>POST /api/ingress/webhooks/{provider}</c>.
/// The facilitator is the single web-exposed point of the deployment, so forge
/// webhooks terminate HERE and fan out to the owning adapter as a
/// <c>command.provider.webhook.v1</c> outbox row addressed to
/// <c>link.&lt;Providers:{provider}:EndpointId&gt;</c>. The kernel stays
/// provider-agnostic: the raw bytes and verbatim headers ride untouched, so
/// HMAC/secret verification stays provider-local inside the adapter's
/// normalizer. Dedupe: <c>webhook:{provider}:{sha256(rawBody)}</c> — providers
/// replay the identical body on retry, so a body-hash key is stable without
/// vendor header parsing. 202 answers immediately; delivery/retry is the
/// dispatcher's existing law. 404 for a missing/disabled provider section,
/// 413 over the size gate, 429 via the rate limiter.
/// </summary>
public static class WebhookIngressEndpoints
{
    /// <summary>Raw-body ceiling for a webhook delivery. Bodies between this
    /// and the envelope contract limit answer 422 schema-violation (the wire
    /// envelope, base64-inflated, can no longer ride the outbox).</summary>
    public const int WebhookMaxBodyBytes = 1_048_576;

    /// <summary>Map <c>POST /api/ingress/webhooks/{provider}</c>. Mounted only
    /// when at least one provider section is enabled (same gate as the human-
    /// plane provider routes): provider-less composition ⇒ no routes.</summary>
    public static IEndpointRouteBuilder MapWebhookIngressApi(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<ProviderRegistry>().AnyEnabled)
        {
            return app;
        }

        var group = app.MapGroup("/api/ingress/webhooks");

        group.MapPost("/{provider}", async (
            string provider,
            HttpRequest request,
            ProviderRegistry registry,
            IDbContextFactory<FacilitatorDbContext> contextFactory,
            EnvelopeValidator validator,
            TimeProvider timeProvider,
            CancellationToken ct) =>
            await IngestAsync(provider, request, registry, contextFactory, validator, timeProvider, ct))
        .WithName("ingestWebhook")
        .RequireRateLimiting("webhook")
        .ProducesProblem(404)
        .ProducesProblem(413)
        .ProducesProblem(429);

        return app;
    }

    private static async Task<IResult> IngestAsync(
        string provider,
        HttpRequest request,
        ProviderRegistry registry,
        IDbContextFactory<FacilitatorDbContext> contextFactory,
        EnvelopeValidator validator,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var token = provider.ToLowerInvariant();
        var options = registry.Find(token);
        if (options is null)
        {
            // Missing section OR enabled=false: same 404 — no oracle for which.
            return Results.Problem(ProblemErrors.Create(
                "provider-unknown", "Not Found", StatusCodes.Status404NotFound,
                $"no enabled Providers:{provider} section owns this delivery"));
        }

        var body = await ReadRawAsync(request, ct);
        if (body is null)
        {
            return Results.Problem(ProblemErrors.PayloadTooLarge(WebhookMaxBodyBytes));
        }

        // Body-hash dedupe key: provider retries replay the identical body.
        var commandId = $"webhook:{token}:{Convert.ToHexString(SHA256.HashData(body))}".ToLowerInvariant();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var json = WebhookEnvelope(token, body, commandId, request, now).ToJsonString();
        if (validator.Validate(json) is { Ok: false } verdict)
        {
            // Base64 inflation can push a legal body past the envelope wire
            // limit: same 413 answer as the sink gate itself.
            return verdict.ErrorClass == "payload-too-large"
                ? Results.Problem(ProblemErrors.PayloadTooLarge(body.Length))
                : Results.Problem(ProblemErrors.SchemaViolation(verdict.Message ?? "webhook envelope rejected"));
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO facilitator.outbox
                 (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, event_type,
                  command_id, envelope, work_id, status, available_at, attempts, created_at)
             VALUES
                 (gen_random_uuid(), {"link." + options.EndpointId}, {"work"}, NULL,
                  {"org.facilitator.command.provider.webhook.v1"},
                  {commandId}, {json}::jsonb, NULL, {"pending"}, {now}, 0, {now})
             ON CONFLICT DO NOTHING
             """, ct);
        await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        await tx.CommitAsync(ct);
        return inserted == 0
            ? Results.Accepted(null, new { status = "duplicate", commandId })
            : Results.Accepted(null, new { status = "queued", commandId });
    }

    /// <summary>Read the raw body under the sink gate; null when over it.
    /// Verbatim bytes are load-bearing: the adapter HMACs over exactly these.</summary>
    private static async Task<byte[]?> ReadRawAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > WebhookMaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > WebhookMaxBodyBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    /// <summary>The fan-out envelope: raw base64 body + verbatim headers. No
    /// vendor vocabulary — the kernel reads no delivery-id header, so
    /// <c>deliveryId</c> stays unset and the adapter derives its own;
    /// <c>data.commandId</c> carries the body-hash dedupe key.</summary>
    internal static JsonObject WebhookEnvelope(string provider, byte[] body, string commandId, HttpRequest request, DateTime now)
    {
        var headers = new JsonObject();
        foreach (var header in request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = "urn:facilitator:kernel",
            ["type"] = "org.facilitator.command.provider.webhook.v1",
            ["subject"] = $"provider/{provider}/webhook",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.provider.webhook.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = commandId,
                ["provider"] = provider,
                ["contentType"] = request.ContentType ?? "application/octet-stream",
                ["body"] = Convert.ToBase64String(body),
                ["headers"] = headers,
            },
        };
    }
}
