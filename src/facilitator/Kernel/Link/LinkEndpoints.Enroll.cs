using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>Enroll (enrollment-secret guard), events (link admission), capabilities.</summary>
public static partial class LinkEndpoints
{
    // Guarded by the enrollment secret (Link:EnrollmentToken), NOT a link token.
    private static void MapEnroll(RouteGroupBuilder group) =>
        group.MapPost("/enroll", async (HttpRequest request, LinkEnrollmentStore store, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
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
            async Task<EnrollmentResult?> Enroll() => await store.EnrollAsync(
                dto.EndpointId, dto.InstanceId, dto.TrustMode, dto.Token,
                teamSlug: "team-alpha", role: "gateway", acceptsDelegatedClaims: false, workload: null, ct);
            try
            {
                try
                {
                    result = await Enroll();
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // Concurrent first-enroll for the same endpoint: the unique
                    // endpoint_id index lost the race; the retry hits the UPDATE arm.
                    result = await Enroll();
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

    private static void MapEvents(RouteGroupBuilder group) =>
        group.MapPost("/events", async (HttpRequest request, LinkIngress ingress, TimeProvider time, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            var admission = await ingress.AdmitAsync(body.Text, ct);
            return admission.ToResult(time.GetUtcNow().UtcDateTime);
        });

    private static void MapCapabilities(RouteGroupBuilder group)
    {
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
            var body = await RequestBodies.ReadAsync(request, ct);
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
    }
}
