using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;
using Facilitator.Kernel.Work;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>Enroll (enrollment-secret guard), events (link admission), capabilities.</summary>
public static partial class LinkEndpoints
{
    // Mode (a): guarded by the enrollment secret (Link:EnrollmentToken). Mode (b):
    // a JWKS-verified OIDC client-credentials JWT bound to a Link:WorkloadIdentities
    // pre-registration (Plan 10). Both mint the same rotating opaque link token.
    private static void MapEnroll(RouteGroupBuilder group) =>
        group.MapPost("/enroll", async (HttpRequest request, LinkEnrollmentStore store, LinkEnrollmentSecret secret, OidcJwks jwks, LinkOptions options, CancellationToken ct) =>
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

            if (dto.Role is not null && !LinkEnrollmentStore.Roles.Contains(dto.Role, StringComparer.Ordinal))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("role must be gateway or child"));
            }

            string team;
            string role;
            bool acceptsDelegated;
            (string Issuer, string Subject)? workload = null;
            var modeB = jwks.IsOidcCandidate(dto.Token, options.EvidenceAudience);
            if (modeB)
            {
                var verified = await jwks.ValidateAsync(dto.Token, options.EvidenceAudience, ct);
                if (!verified.Valid)
                {
                    return Results.Problem(ProblemErrors.WorkloadTokenInvalid(verified.Reason!));
                }

                var clientId = OidcClaims.String(verified.Payload!.Value, "azp");
                var binding = options.WorkloadIdentities.FirstOrDefault(b =>
                    string.Equals(b.ClientId, clientId, StringComparison.Ordinal)
                    && string.Equals(b.EndpointId, dto.EndpointId, StringComparison.Ordinal));
                if (binding is null)
                {
                    return Results.Problem(ProblemErrors.WorkloadNotRegistered(clientId ?? "<no azp>"));
                }

                if (dto.Role is not null && !string.Equals(dto.Role, binding.Role, StringComparison.Ordinal))
                {
                    return Results.Problem(ProblemErrors.RoleMismatch(
                        $"body role '{dto.Role}' contradicts the binding role '{binding.Role}'"));
                }

                team = binding.Team;
                role = binding.Role;
                acceptsDelegated = binding.AcceptsDelegatedClaims;
                workload = (verified.Token!.Issuer, clientId!);
            }
            else
            {
                // Mode (a): shared-secret enrollment — auth (401) precedes
                // authorization (403). The body may name a role (default
                // gateway: pre-Plan-10 compat); the team must be named and
                // roster-present: no default-team fallback.
                if (!secret.Matches(dto.Token))
                {
                    return Results.Problem(ProblemErrors.EnrollmentSecretInvalid());
                }

                if (string.IsNullOrWhiteSpace(dto.Team))
                {
                    return Results.Problem(ProblemErrors.TeamUnknown("<missing>"));
                }

                team = dto.Team;
                role = dto.Role ?? "gateway";
                acceptsDelegated = false;
            }

            // gw. prefix law: the Gateway channel is reserved for gateway-role
            // enrollments — identity+role come from enrollment, never a manifest.
            if (dto.EndpointId.StartsWith("gw.", StringComparison.Ordinal) && role != "gateway")
            {
                return Results.Problem(ProblemErrors.RoleMismatch(
                    $"endpointId '{dto.EndpointId}' is on the gw. Gateway channel and requires role gateway"));
            }

            EnrollmentResult? result;
            async Task<EnrollmentResult?> Enroll() => await store.EnrollAsync(
                dto.EndpointId, dto.InstanceId, dto.TrustMode, dto.Token,
                team, role, acceptsDelegated, workload, ct);
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

            if (result?.RoleMismatch == true)
            {
                return Results.Problem(ProblemErrors.RoleMismatch(
                    $"rotation may not change the role of '{dto.EndpointId}' (revoke + re-enroll)"));
            }

            if (result is null)
            {
                var known = await store.TeamExistsAsync(team, ct);
                return Results.Problem(known
                    ? ProblemErrors.EnrollmentSecretInvalid()
                    : ProblemErrors.TeamUnknown(team));
            }

            return Results.Created($"/api/link/v1/capabilities",
                new EnrollResponseDto(result.EnrollmentId.ToString("N"), result.TrustMode, result.LinkToken, result.ExpiresAt));
        })
        .RequireRateLimiting("enroll");

    private static void MapEvents(RouteGroupBuilder group) =>
        group.MapPost("/events", async (HttpRequest request, HttpContext http, LinkIngress ingress, TimeProvider time, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            var admission = await ingress.AdmitAsync(body.Text, LinkAuthMiddleware.RequirePrincipal(http), ct);
            return admission.ToResult(time.GetUtcNow().UtcDateTime);
        });

    // Workload-plane upload: the guard is the link token (this route lives
    // under the guarded /api/link/v1 prefix, so LinkAuthMiddleware already
    // classified it and the role gate skipped it) + row team ownership in
    // the ledger; foreign rows answer 404.
    private static void MapArtifactContent(RouteGroupBuilder group) =>
        group.MapPut("/artifacts/{id:guid}/content",
            async (Guid id, HttpRequest request, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var workload = LinkAuthMiddleware.RequirePrincipal(http);
                var scoped = await ledger.GetForWorkloadAsync(workload, id, ct);
                if (scoped is null)
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return ArtifactEndpoints.StoreResultAsync(
                    await ledger.StoreAsync(id, request.Body, workload.TeamSlug, workload, ct), id);
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
