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
    // pre-registration. Both mint the same rotating opaque link token.
    private static void MapEnroll(RouteGroupBuilder group) =>
        group.MapPost("/enroll", async (HttpRequest request, LinkEnrollmentStore store, EnrollmentTokenStore tokens, LinkEnrollmentSecret secret, OidcJwks jwks, LinkOptions options, CancellationToken ct) =>
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
            string enrollSecret = dto.Token;
            Guid? mintedId = null;
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
                // roster-present: no default-team fallback. A presented
                // minted enrollment token (facilitator.enrollment_tokens)
                // ignores the static secret entirely: the row's team/role
                // bind the enrollment, the row's consumption fences the
                // endpoint (re-enroll only on the consuming endpoint).
                role = dto.Role ?? "gateway";
                acceptsDelegated = false;
                var decision = await tokens.DecideAsync(dto.Token ?? string.Empty, dto.Team ?? string.Empty, role, dto.EndpointId, ct);
                if (decision.Outcome == MintedTokenOutcome.NotMinted)
                {
                    // Hash miss: the static-secret arm decides, byte-identical.
                    if (!secret.Matches(dto.Token))
                    {
                        return Results.Problem(ProblemErrors.EnrollmentSecretInvalid());
                    }

                    if (string.IsNullOrWhiteSpace(dto.Team))
                    {
                        return Results.Problem(ProblemErrors.TeamUnknown("<missing>"));
                    }

                    team = dto.Team;
                }
                else if (decision.Outcome == MintedTokenOutcome.Ok)
                {
                    team = decision.Team!;
                    role = decision.Role!;
                    enrollSecret = secret.Literal; // the store guard replays the static secret
                    mintedId = decision.WasConsumed ? null : decision.TokenId;
                }
                else if (decision.Outcome is MintedTokenOutcome.Invalid or MintedTokenOutcome.EndpointMismatch)
                {
                    return Results.Problem(ProblemErrors.EnrollmentSecretInvalid());
                }
                else if (decision.Outcome == MintedTokenOutcome.RoleMismatch)
                {
                    return Results.Problem(ProblemErrors.RoleMismatch(
                        $"the minted enrollment token binds role '{decision.Role}', not '{role}'"));
                }
                else
                {
                    return Results.Problem(ProblemErrors.TeamMismatch(dto.Team ?? "<missing>"));
                }
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
                dto.EndpointId, dto.InstanceId, dto.TrustMode, enrollSecret,
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

            if (mintedId is not null && !await tokens.TryConsumeAsync(mintedId.Value, dto.EndpointId, result.EnrollmentId, ct))
            {
                // Concurrent first use won the consume fence: the token is
                // that endpoint's standing secret now, not this one's.
                return Results.Problem(ProblemErrors.EnrollmentSecretInvalid());
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

    // Workload-plane content READ: the guard is the exact pair the content PUT
    // uses (link token + row team ownership in the ledger), so a link child
    // dereferences its own uploads and the refs it was assigned — foreign or
    // missing rows answer 404 (non-disclosure). Body shape mirrors the
    // human-plane read: presigned 302 for object-store backends (portable
    // retrieval), streamed bytes otherwise, 404 when the row says stored but
    // the blob is gone. Team law: a cross-team row (e.g. a chat image stored
    // by the communication adapter's team, routed to a Work on another team)
    // answers 404 to that team's workload — honest, not a leak.
    private static void MapArtifactRead(RouteGroupBuilder group) =>
        group.MapGet("/artifacts/{id:guid}/content",
            async (Guid id, IArtifactLedger ledger, IArtifactStore store, HttpContext http, CancellationToken ct) =>
            {
                var workload = LinkAuthMiddleware.RequirePrincipal(http);
                var scoped = await ledger.GetForWorkloadAsync(workload, id, ct);
                if (scoped is not { State: "stored" } || !store.Owns(scoped.StorageUri))
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                if (store is IStoreSignedUrlSource source
                    && await source.PresignDownloadAsync(
                        ArtifactKeys.StoreKeyOf(scoped.StorageUri), scoped.MediaType, ArtifactTicket.AllocationTtl, ct) is { } url)
                {
                    return Results.Redirect(url.ToString(), permanent: false);
                }

                var stream = await store.GetAsync(ArtifactKeys.StoreKeyOf(scoped.StorageUri), ct);
                if (stream is null)
                {
                    // Row says stored but the blob is gone: absent, like any other 404.
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return Results.Stream(stream, scoped.MediaType,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{scoped.Digest}\""));
            });

    // Direct-upload confirmation on the workload plane: same guard shape as
    // the content PUT (link token + row team ownership in the ledger).
    private static void MapArtifactConfirm(RouteGroupBuilder group) =>
        group.MapPost("/artifacts/{id:guid}/confirm",
            async (Guid id, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var workload = LinkAuthMiddleware.RequirePrincipal(http);
                var scoped = await ledger.GetForWorkloadAsync(workload, id, ct);
                if (scoped is null)
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return ArtifactEndpoints.ConfirmResultAsync(
                    await ledger.ConfirmAsync(id, workload.TeamSlug, workload, ct), id);
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
