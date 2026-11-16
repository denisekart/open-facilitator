using System.Text.Json;
using System.Text.Json.Serialization;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;


namespace Facilitator.Kernel.Link;

/// <summary>
/// The credential endpoints: <c>challenge</c> (a single-use nonce, unauthenticated
/// by design), <c>proof</c> (a detached EdDSA JWS in, a 5-minute session JWT
/// out), and <c>enroll/{endpointId}/rotate</c> (swap your own key under proof by
/// both the old and the new key). Mounted in the same <c>/api/link/v1</c> group
/// as enroll, with the same fixed-window budget line.
/// </summary>
public static partial class LinkEndpoints
{
    private static void MapProofApi(RouteGroupBuilder group)
    {
        group.MapPost("/challenge", async (
            HttpRequest request, LinkEnrollmentStore store, LinkOptions options, AuditWriter audit,
            TimeProvider time, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            string? endpointId;
            try
            {
                endpointId = JsonSerializer.Deserialize<ChallengeRequestDto>(body.Text, Web)?.EndpointId;
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 128)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("endpointId is required (at most 128 characters)"));
            }

            // A challenge proves nothing, so it cannot be guarded by what it is
            // issuing — and an unknown endpoint gets the same 401 a wrong proof
            // gets: the no-existence-oracle law.
            //
            // The nonce is `<epoch>.<seq>.<24 random bytes, base64url>`:
            // The client must sign the epoch AND the fence it was issued under,
            // and the response stays exactly {nonce, publicKeyRequired,
            // algorithm, expiresAt}, so both travel inside the opaque handle
            // rather than as new fields.
            var fence = await store.PeekFencesAsync([endpointId], ct);
            if (!fence.TryGetValue(endpointId, out var row) || row.Dead)
            {
                // The challenge route needs no bearer, so the refusal names the
                // real reason (no live enrollment) — an unknown endpoint and a
                // revoked one answer identically, so nothing about the set leaks.
                return Results.Problem(ProblemErrors.Create("unauthorized", "Unauthorized", StatusCodes.Status401Unauthorized,
                    "No live enrollment for this endpoint."));
            }

            var nonce = row.Epoch + "." + row.Seq + "." + LinkKeys.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            var expiresAt = time.GetUtcNow().UtcDateTime.AddSeconds(options.ChallengeLifetimeSeconds);
            if (!await store.IssueChallengeAsync(endpointId, nonce, expiresAt, ct))
            {
                return Results.Problem(ProblemErrors.Create("unauthorized", "Unauthorized", StatusCodes.Status401Unauthorized,
                    "No live enrollment for this endpoint."));
            }

            await audit.WriteAsync(new AuditRecord(
                null, "link.challenge-issued", "/api/link/v1/challenge", "allow", null, null, null,
                JsonSerializer.Serialize(new { endpointId, expiresAt })), ct);
            return Results.Ok(new ChallengeResponseDto(nonce, true, "ed25519", expiresAt));
        })
        .RequireRateLimiting("enroll");

        group.MapPost("/proof", async (
            HttpRequest request, LinkProofVerifier verifier, LinkEnrollmentStore store,
            OidcJwks jwks, LinkOptions options, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            ProofRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ProofRequestDto>(body.Text, Web);
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            // Class `oidc` authenticates by issuer trust, not by a key: the same
            // route takes a JWKS-verified workload token and mints the same
            // 5-minute session. One route, one session shape, two identities.
            if (dto?.Token is { Length: > 0 } workloadToken && jwks.IsOidcCandidate(workloadToken, options.EvidenceAudience))
            {
                var verified = await jwks.ValidateAsync(workloadToken, options.EvidenceAudience, ct);
                if (!verified.Valid || verified.Payload is not { } payload)
                {
                    return Results.Problem(ProblemErrors.ProofInvalid(verified.Reason ?? "workload token rejected"));
                }

                // The workload identity is the SAME pair enrollment bound: issuer plus
                // the CLIENT (azp), with `sub` only as the fallback for a token with no
                // authorized-party claim. Reading `sub` alone would look the workload up
                // under Keycloak's service-account subject and miss every binding.
                var issuer = OidcClaims.String(payload, "iss");
                var sub = OidcClaims.String(payload, "azp") ?? OidcClaims.String(payload, "sub");
                var link = issuer is null || sub is null
                    ? null
                    : await store.FindWorkloadEnrollmentAsync(issuer, sub, ct);
                if (link is null || link.Dead)
                {
                    return Results.Problem(ProblemErrors.ProofInvalid("no live enrollment for this workload identity"));
                }

                var oidcMint = await verifier.MintWorkloadSessionAsync(link, ct);
                return Results.Ok(new ProofResponseDto(oidcMint.SessionToken, oidcMint.ExpiresAt, oidcMint.Seq));
            }

            var proof = dto?.Proof;

            var htu = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}";
            var verdict = await verifier.VerifyAsync(proof, "POST", htu, CbhOf(http), ct);
            if (!verdict.Accepted)
            {
                return verdict.Failure switch
                {
                    ProofFailure.ReplaySuspect => Results.Problem(ProblemErrors.ReplaySuspect(verdict.Claims?.Issuer ?? "<unknown>")),
                    _ => Results.Problem(ProblemErrors.ProofInvalid(verdict.Reason)),
                };
            }

            var subject = verdict.Subject!;
            var mint = await verifier.MintSessionAsync(subject, verdict.Claims!, ct);

            // The proof consumed the fence's freshness; a session minted from a
            // seq the world has moved past is a replay even mid-verification.
            if (!await store.AdvanceSeqAsync(subject.EndpointId, mint.Seq, ct))
            {
                return Results.Problem(ProblemErrors.ReplaySuspect(subject.EndpointId));
            }

            return Results.Ok(new ProofResponseDto(mint.SessionToken, mint.ExpiresAt, mint.Seq));
        })
        .RequireRateLimiting("enroll");

        group.MapPost("/enroll/{endpointId}/rotate", async (
            string endpointId, HttpRequest request, HttpContext http, LinkEnrollmentStore store,
            LinkProofVerifier verifier, LinkOptions options, AuditWriter audit,
            TimeProvider time, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            RotateRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<RotateRequestDto>(body.Text, Web);
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            var principal = LinkAuthMiddleware.RequirePrincipal(http);
            if (dto is null || string.IsNullOrWhiteSpace(dto.OldKeyId) || string.IsNullOrWhiteSpace(dto.NewPublicKey)
                || dto.Proofs is not { Length: 2 } proofs)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "oldKeyId, newPublicKey and exactly two proofs are required"));
            }

            // The endpoint path is the identity: you rotate your own keys.
            if (!string.Equals(endpointId, principal.EndpointId, StringComparison.Ordinal))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("endpointId must be the authenticated endpoint"));
            }

            if (!LinkProof.TryParseRotationProof(proofs[0], out var first, out var detail)
                || !LinkProof.TryParseRotationProof(proofs[1], out var second, out detail)
                || first is null || second is null)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(detail!));
            }

            // Both proofs sign the SAME payload — mutual agreement, not takeover.
            if (!first.Equals(second))
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "both proofs must sign the same payload {oldKid, newKid, nonce, seq}"));
            }

            if (!LinkKeys.TryParsePublicKey(dto.NewPublicKey, out var newRaw))
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "newPublicKey must be one raw Ed25519 key in base64url"));
            }

            var newKeyId = LinkKeys.KeyId(newRaw);
            var subject = await store.GetProofSubjectAsync(endpointId, first.OldKid, ct);
            if (subject is null || subject.Dead)
            {
                return Results.Problem(ProblemErrors.Create("unauthorized", "Unauthorized", StatusCodes.Status401Unauthorized,
                    "A valid Bearer session JWT is required."));
            }

            // Proof by the old key (active, or retired inside the grace window)
            // and proof by the new key (proves the swap is agreed, not stolen).
            var oldOk = subject.ClaimedKeyPublicKey is { Length: > 0 } oldKey
                && LinkProof.VerifySignature(proofs[0], oldKey);
            var newOk = LinkProof.VerifySignature(proofs[1], dto.NewPublicKey);
            if (!oldOk || !newOk)
            {
                await audit.WriteAsync(new AuditRecord(
                    principal.EndpointId, "link.proof-failed", $"/api/link/v1/enroll/{endpointId}/rotate", "deny",
                    "rotate-proof-signature", null, null,
                    JsonSerializer.Serialize(new { endpointId, oldKeyId = dto.OldKeyId, newKeyId })), ct);
                return Results.Problem(ProblemErrors.ProofInvalid("rotation proofs are not signed by the named keys"));
            }

            // The swap consumes a fresh challenge: replaying a rotation needs a new nonce.
            if (!await store.ConsumeChallengeAsync(endpointId, first.Nonce, ct))
            {
                return Results.Problem(ProblemErrors.ProofInvalid("nonce absent, expired or already used"));
            }

            var grace = TimeSpan.FromMinutes(options.RotateGraceMinutes);
            var outcome = await store.RotateKeyAsync(
                endpointId, first.OldKid, newKeyId, dto.NewPublicKey, first.Seq, grace,
                principal.EndpointId, ct);
            return outcome switch
            {
                RotateKeyOutcome.Rotated or RotateKeyOutcome.GraceConsumed => Results.NoContent(),
                RotateKeyOutcome.GraceExpired => Results.Problem(ProblemErrors.KeyRotationGraceExpired(endpointId)),
                RotateKeyOutcome.ReplaySuspect => Results.Problem(ProblemErrors.ReplaySuspect(endpointId)),
                _ => Results.Problem(ProblemErrors.ProofInvalid("old key is not an active or grace-window key of this endpoint")),
            };
        });
    }

    /// <summary>Channel-binding hash for this request: the TLS exporter when the
    /// transport exposes one, the empty string otherwise (always present as a
    /// claim; <c>Link:RequireChannelBinding</c> makes empty fatal).</summary>
    internal static string CbhOf(HttpContext http) =>
        LinkChannelBinding.HashOf(http.Features);
}

/// <summary>Wire shape of POST /api/link/v1/challenge.</summary>
public sealed record ChallengeRequestDto([property: JsonPropertyName("endpointId")] string EndpointId);

/// <summary>Wire shape of the 200 challenge response.</summary>
public sealed record ChallengeResponseDto(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("publicKeyRequired")] bool PublicKeyRequired,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt);

/// <summary>Wire shape of POST /api/link/v1/proof.</summary>
public sealed record ProofRequestDto(
    [property: JsonPropertyName("proof")] string? Proof,
    [property: JsonPropertyName("token")] string? Token = null);

/// <summary>Wire shape of the 200 proof response: the 5-minute session JWT.</summary>
public sealed record ProofResponseDto(
    [property: JsonPropertyName("sessionToken")] string SessionToken,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt,
    [property: JsonPropertyName("seq")] long Seq);

/// <summary>Wire shape of POST /api/link/v1/enroll/{endpointId}/rotate: exactly
/// two detached JWSs, both signing <c>{oldKid, newKid, nonce, seq}</c>.</summary>
public sealed record RotateRequestDto(
    [property: JsonPropertyName("oldKeyId")] string? OldKeyId,
    [property: JsonPropertyName("newPublicKey")] string? NewPublicKey,
    [property: JsonPropertyName("proofs")] string[]? Proofs);
