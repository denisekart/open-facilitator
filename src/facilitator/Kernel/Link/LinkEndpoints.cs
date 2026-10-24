using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Ingress;
using Facilitator.Kernel.Persistence;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

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
public static class LinkEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Map <c>/api/link/v1</c> (link-token auth via LinkAuthMiddleware) and kernel-token extras.</summary>
    public static IEndpointRouteBuilder MapLinkApi(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        var group = app.MapGroup("/api/link/v1");

        // SignalR hub (default reference transport). Bearer auth is the
        // "facilitator-link" scheme — the SAME ILinkTokenValidator seam as
        // every HTTP surface here.
        app.MapHub<LinkHub>("/hubs/link");

        // Guarded by the enrollment secret (Link:EnrollmentToken), NOT a link token.
        group.MapPost("/enroll", async (HttpRequest request, LinkEnrollmentStore store, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            EnrollRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<EnrollRequestDto>(body.Text, Web);
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.EndpointId)
                || string.IsNullOrWhiteSpace(dto.InstanceId) || string.IsNullOrWhiteSpace(dto.TrustMode))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("token, endpointId, instanceId and trustMode are required"));
            }

            // $defs/idToken bound, server-side: both identity fields are idTokens.
            if (dto.EndpointId.Length > 128 || dto.InstanceId.Length > 128)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("endpointId and instanceId must be at most 128 characters"));
            }
            EnrollmentResult? result;
            try
            {
                try
                {
                    result = await store.EnrollAsync(dto.EndpointId, dto.InstanceId, dto.TrustMode, dto.Token, ct);
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // Concurrent first-enroll for the same endpoint: the unique
                    // endpoint_id index lost the race; the retry hits the UPDATE arm.
                    result = await store.EnrollAsync(dto.EndpointId, dto.InstanceId, dto.TrustMode, dto.Token, ct);
                }
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(ex.Message));
            }

            if (result is null)
            {
                return Results.Problem(ProblemErrors.EnrollmentSecretInvalid());
            }

            return Results.Created($"/api/link/v1/capabilities",
                new EnrollResponseDto(result.EnrollmentId.ToString("N"), result.TrustMode, result.LinkToken, result.ExpiresAt));
        })
        .RequireRateLimiting("enroll");

        group.MapPost("/events", async (HttpRequest request, LinkIngress ingress, TimeProvider time, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            var admission = await ingress.AdmitAsync(body.Text, ct);
            return admission.ToResult(time.GetUtcNow().UtcDateTime);
        });

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

        group.MapPost("/deliveries/{deliveryId:guid}/receipt", async (
            Guid deliveryId,
            HttpRequest request,
            LinkLedger ledger,
            LinkConnectionRegistry registry,
            LinkDeliveryPump pump,
            LinkOptions options,
            ControlFrameValidator frames,
            CancellationToken ct) =>
        {
            var principal = LinkAuthMiddleware.RequirePrincipal(request.HttpContext);
            var body = await ReadBodyAsync(request, ct);
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

            var state = await ledger.StateAsync(deliveryId, ct);
            if (state is null || state.Value.EndpointId != principal.EndpointId)
            {
                return Results.Problem(ProblemErrors.DeliveryNotFound(deliveryId));
            }

            // Idempotent on an already-acked delivery (204 again; the receipt
            // jsonb is overwritten, the timeline-free ledger is truth). A
            // 'duplicate' receipt terminates redelivery just like 'accepted'.
            await ledger.AckAsync(deliveryId, principal.EndpointId, frame.GetRawText(), ct);
            await pump.PumpEndpointAsync(principal.EndpointId, options.MaxInFlight, ct);
            registry.Wake(principal.EndpointId);
            return Results.NoContent();
        });

        group.MapGet("/capabilities", async (LinkEnrollmentStore store, HttpContext http, CancellationToken ct) =>
        {
            var principal = LinkAuthMiddleware.RequirePrincipal(http);
            var stored = await store.GetCapabilitiesAsync(principal.EndpointId, ct);
            if (stored is null || stored.Value.Manifest is null)
            {
                return Results.Problem(ProblemErrors.CapabilitiesNotFound(principal.EndpointId));
            }

            return Results.Json(JsonDocument.Parse(stored.Value.Manifest).RootElement, Web);
        });

        group.MapPost("/capabilities", async (HttpRequest request, LinkEnrollmentStore store, ControlFrameValidator _, CancellationToken ct) =>
        {
            var principal = LinkAuthMiddleware.RequirePrincipal(request.HttpContext);
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            JsonElement manifest;
            try
            {
                using var doc = JsonDocument.Parse(body.Text.Length == 0 ? "{}" : body.Text);
                manifest = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            if (!CapabilityManifestValidator.Validate(manifest, out var violation))
            {
                return Results.Problem(ProblemErrors.SchemaViolation(violation!));
            }

            var ownerTeam = manifest.GetProperty("ownerTeam").GetString()!;
            var stored = await store.GetCapabilitiesAsync(principal.EndpointId, ct);
            if (stored is null || !string.Equals(ownerTeam, stored.Value.TeamSlug, StringComparison.Ordinal))
            {
                return Results.Problem(ProblemErrors.TeamMismatch(ownerTeam));
            }

            await store.StoreCapabilitiesAsync(
                principal.EndpointId, manifest.GetRawText(), LinkIngress.DigestOf(manifest.GetRawText()), ct);
            return Results.NoContent();
        });

        // Kernel-token surface (BootstrapTokenMiddleware still guards /api/*
        // outside /api/link/v1): dead-letter visibility (appendix §5.6).
        app.MapGroup("/api").MapGet("/dead-letters", async (Shared.Persistence.OutboxAdminStore admin, CancellationToken ct) =>
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
            app.MapGroup("/api").MapPost("/dev/link-outbox", async (HttpRequest request, Shared.Persistence.OutboxAdminStore admin, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync(request, ct);
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

        return app;
    }

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
        // Inline materialization before the claim (locked: the pump also runs
        // "inline before a /deliveries claim"). The pump never claims for a
        // socketless endpoint, so this call's own ClaimAsync below is the
        // single claimant — a pre-claim pump pass must not steal the window.
        await pump.MaterializeForClaimAsync(endpointId, ct);
        var claimed = await ledger.ClaimAsync(endpointId, epoch, limit(request) ?? 16, options.MaxInFlight, ct);
        registry.Wake(endpointId);
        return claimed;
    }

    private static int wait(HttpRequest request) =>
        request.Query.TryGetValue("wait", out var w) && int.TryParse(w, out var parsed) ? Math.Clamp(parsed, 0, 60) : 0;

    private static int? limit(HttpRequest request)
    {
        if (!request.Query.TryGetValue("limit", out var l))
        {
            return 16;
        }

        return int.TryParse(l, out var parsed) && parsed is >= 1 and <= 64 ? parsed : null;
    }

    private sealed record Body(string Text, int Bytes, bool TooLarge);

    private static async Task<Body> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        const int limit = IngressProcessor.MaxBodyBytes;
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        if (request.ContentLength > limit)
        {
            return new Body(string.Empty, (int)request.ContentLength!.Value, true);
        }

        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            var bytes = Encoding.UTF8.GetByteCount(text);
            return new Body(text, bytes, bytes > limit);
        }
        catch (BadHttpRequestException)
        {
            return new Body(string.Empty, limit + 1, true);
        }
    }
}

/// <summary>Capability-manifest gate for POST /capabilities (schema + shape).</summary>
file static class CapabilityManifestValidator
{
    /// <summary>Validate against capability-manifest.v1.json via the shared frame validator's registry.</summary>
    public static bool Validate(JsonElement manifest, out string? violation)
    {
        var verdict = ControlFrameValidator.CreateDefault().ValidateCapabilityManifest(manifest);
        violation = verdict.Ok ? null : verdict.Message;
        return verdict.Ok;
    }
}
