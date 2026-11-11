using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The frozen HTTP fallback surface (protocol/openapi/link-v1.yaml, 1.0.1).
/// Endpoints stay thin; the envelope admission path is the SAME
/// <see cref="LinkIngress.AdmitAsync"/> the hub's Envelope method calls, so
/// both transports answer identically.
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
        MapArtifactRead(group);
        MapArtifactConfirm(group);
        MapDeliveries(group);
        MapReceipt(group);
        MapCapabilities(group);
        MapQueueReason(group);
        MapDevAffordances(app, environment);

        return app;
    }

    // D2/P13: a gateway observes capacity and says so - an advisory reason on
    // one of its team's assignment lines (`waiting-for-capacity` is the law's
    // name). Observational only: no state moves, and the reason rides the
    // assignment's timeline (`assignment.queue-reason`), rendered distinctly
    // from `running` by the work's execution read.
    private static void MapQueueReason(RouteGroupBuilder group) =>
        group.MapPost("/assignments/{assignmentId:guid}/queue-reason",
            async (Guid assignmentId, HttpRequest request, HttpContext http, TimeProvider time, CancellationToken ct) =>
            {
                var principal = LinkAuthMiddleware.RequirePrincipal(http);
                if (!string.Equals(principal.Role, "gateway", StringComparison.Ordinal))
                {
                    return Results.Problem(ProblemErrors.Create(
                        "gateway-only", "Forbidden", StatusCodes.Status403Forbidden,
                        "only a gateway reports queue reasons"));
                }

                var body = await RequestBodies.ReadAsync(request, ct);
                if (body.TooLarge)
                {
                    return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
                }

                return await RequestBodyJson.ParseAsync(body, async document =>
                {
                    var reason = document.RootElement.TryGetProperty("queueReason", out var q)
                        && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
                    await using var db = await http.RequestServices
                        .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Facilitator.Kernel.Shared.Persistence.FacilitatorDbContext>>()
                        .CreateDbContextAsync(ct);
                    var assignment = await db.Assignments.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId, ct);
                    if (assignment is null || assignment.TeamId != principal.TeamId)
                    {
                        return Results.Problem(ProblemErrors.Create(
                            "assignment-unknown", "Not found", StatusCodes.Status404NotFound, "no such assignment for this gateway's team"));
                    }

                    var workId = assignment.WorkId;
                    var version = await db.Works.Where(w => w.WorkId == workId)
                        .Select(w => w.Version).FirstAsync(ct);
                    db.WorkEvents.Add(new Facilitator.Kernel.Shared.Persistence.WorkEventEntity
                    {
                        EventId = Guid.NewGuid(),
                        WorkId = workId,
                        WorkVersion = version,
                        Kind = "assignment.queue-reason",
                        Actor = $"link:{principal.EndpointId}",
                        Detail = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            assignmentId = assignmentId.ToString(),
                            queueReason = reason,
                        }),
                        CreatedAt = time.GetUtcNow().UtcDateTime,
                    });
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                });
            });

    // Dev-only test affordance: seed a link-addressed outbox row so link
    // delivery flows are provable end-to-end without a full assignment slice.
    private static void MapDevAffordances(IEndpointRouteBuilder app, IHostEnvironment environment)
    {
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
