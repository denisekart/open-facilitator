using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The frozen HTTP fallback surface (protocol/openapi/link-v1.yaml, 1.0.1)
/// plus the kernel-token dead-letter view. Endpoints stay thin; the envelope
/// admission path is the SAME <see cref="LinkIngress.AdmitAsync"/> the hub's
/// Envelope method calls, so both transports answer identically.
/// HTTP principals get no WelcomeFrame: enrollment + bearer token IS the
/// session bootstrap, and every deliveries call implicitly refreshes the
/// lease — a 404 <c>session-not-found</c> without a live <c>link_sessions</c>
/// row (enroll + hub handshake first).
/// </summary>
public static partial class LinkEndpoints
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Map <c>/api/link/v1</c> (link-token auth via LinkAuthMiddleware) and kernel-token extras.</summary>
    public static IEndpointRouteBuilder MapLinkApi(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        var group = app.MapGroup("/api/link/v1");

        // SignalR hub (default reference transport). Bearer auth is the
        // "facilitator-link" scheme — the SAME ILinkTokenValidator seam as
        // every HTTP surface here.
        app.MapHub<LinkHub>("/hubs/link");

        MapEnroll(group);
        MapEvents(group);
        MapArtifactContent(group);
        MapArtifactConfirm(group);
        MapDeliveries(group);
        MapReceipt(group);
        MapCapabilities(group);
        MapAdmin(app, environment);

        return app;
    }

    // Identity-gated surface (the role gate guards /api/* outside
    // /api/link/v1): dead-letter visibility (appendix §5.6).
    private static void MapAdmin(IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        app.MapGroup("/api").MapGet("/dead-letters", async (OutboxAdminStore admin, CancellationToken ct) =>
        {
            var rows = await admin.ListDeadLettersAsync(50, ct);
            return Results.Ok(rows
                .Select(d => new DeadLetterDto(d.DeadLetterId, d.OutboxId, d.Reason, JsonDocument.Parse(d.Envelope).RootElement.Clone(), d.CreatedAt))
                .ToArray());
        });

        // Test affordance (bootstrap-kernel-token auth, Development only):
        // insert a link-addressed outbox row so link delivery flows are
        // provable end-to-end without a full assignment slice.
        if (environment.IsDevelopment())
        {
            app.MapGroup("/api").MapPost("/dev/link-outbox", async (HttpRequest request, OutboxAdminStore admin, CancellationToken ct) =>
            {
                var body = await RequestBodies.ReadAsync(request, ct);
                if (body.TooLarge)
                {
                    return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
                }

                using var doc = JsonDocument.Parse(body.Text.Length == 0 ? "{}" : body.Text);
                var root = doc.RootElement;
                var endpointId = root.TryGetProperty("endpointId", out var e) ? e.GetString() : null;
                if (string.IsNullOrWhiteSpace(endpointId) || !root.TryGetProperty("envelope", out var envelope))
                {
                    return Results.Problem(ProblemErrors.SchemaViolation("endpointId and envelope are required"));
                }

                var type = envelope.TryGetProperty("type", out var t) ? t.GetString() ?? "org.facilitator.command.work.assign.v1" : "org.facilitator.command.work.assign.v1";
                var commandId = envelope.TryGetProperty("data", out var data) && data.TryGetProperty("commandId", out var cid)
                    ? cid.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");
                var outboxId = await admin.AppendDevWorkAsync($"link.{endpointId}", type, commandId, envelope.GetRawText(), ct);
                return Results.Accepted(null, new { outboxId });
            });
        }
    }
}
