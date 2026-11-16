using System.Text.Json;

using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

namespace Facilitator.Kernel.Link;

/// <summary>A verified proof plus the enrollment state it was verified against.</summary>
public sealed record ProofVerdict(
    bool Accepted,
    ProofFailure Failure,
    string Reason,
    LinkProofClaims? Claims,
    ProofSubject? Subject);

/// <summary>A minted session.</summary>
public sealed record SessionMint(string SessionToken, DateTime ExpiresAt, long Seq, string SessionKeyId);

/// <summary>
/// Verifies detached EdDSA proofs of possession — every check fail-closed,
/// every denial audited (hash/fingerprint-only detail, never key bytes) — and
/// mints the 5-minute session JWT a verified proof buys. Shared by the HTTP
/// proof endpoint and the hub's hello-carried handshake proof, so the two
/// transports cannot drift apart on what a valid proof is.
/// </summary>
public sealed class LinkProofVerifier(
    LinkEnrollmentStore store,
    LinkSessionKeys sessionKeys,
    LinkOptions options,
    AuditWriter audit,
    TimeProvider timeProvider,
    ILogger<LinkProofVerifier> logger)
{
    /// <summary>
    /// The full check list: structural parse, signature by the enrollment's
    /// ACTIVE key, htm/htu match, iat/exp inside the configured skew, nonce
    /// present-and-unconsumed, jti unseen, seq at or above the fence, epoch
    /// equal to the row's, cbh non-empty when required.
    /// </summary>
    public async Task<ProofVerdict> VerifyAsync(
        string? proof, string htm, string htu, string? cbh, CancellationToken ct, string resource = "/api/link/v1/proof")
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!LinkProof.TryParse(proof, out var claims, out var parseDetail))
        {
            return await DeniedAsync(claims, ProofFailure.Malformed, parseDetail ?? "malformed proof", null, ct, resource);
        }

        var proofClaims = claims!;
        var subject = await store.GetProofSubjectAsync(proofClaims.Issuer, proofClaims.Sub, ct);
        if (subject is null)
        {
            // Same shape as a wrong proof: no existence oracle.
            return await DeniedAsync(claims, ProofFailure.UnknownEndpoint, "unknown endpoint or key", null, ct, resource);
        }

        if (subject.Dead)
        {
            return await DeniedAsync(claims, ProofFailure.Epoch, "enrollment revoked or expired", subject, ct, resource);
        }

        if (subject.CredentialClass == "oidc")
        {
            // Mode (b) authenticates by issuer trust, not by a key it holds.
            return await DeniedAsync(claims, ProofFailure.Signature, "credential class oidc does not prove keys", subject, ct, resource);
        }

        if (subject.ClaimedKeyState is not "active"
            || subject.ClaimedKeyPublicKey is not { Length: > 0 } activeKey
            || !LinkProof.VerifySignature(proof!, activeKey))
        {
            return await DeniedAsync(claims, ProofFailure.Signature, "signature is not by the active key", subject, ct, resource);
        }

        if (!string.Equals(proofClaims.Htm, htm, StringComparison.Ordinal)
            || !string.Equals(ChannelUri(proofClaims.Htu), ChannelUri(htu), StringComparison.Ordinal))
        {
            return await DeniedAsync(claims, ProofFailure.ChannelMismatch, "htm/htu do not match this request", subject, ct, resource);
        }

        var skew = TimeSpan.FromSeconds(options.EvidenceClockSkewSeconds);
        if (proofClaims.IssuedAt - skew > now || proofClaims.Exp + skew < now)
        {
            return await DeniedAsync(claims, ProofFailure.Clock, "iat/exp outside the allowed skew", subject, ct, resource);
        }

        if (options.RequireChannelBinding && proofClaims.Cbh.Length == 0)
        {
            return await DeniedAsync(claims, ProofFailure.ChannelBinding, "channel binding required but empty", subject, ct, resource);
        }

        if (proofClaims.Epoch != subject.Epoch)
        {
            return await DeniedAsync(claims, ProofFailure.Epoch, "epoch does not match the enrollment", subject, ct, resource);
        }

        // The nonce carries the state it was minted against
        // (`<epoch>.<seq>.<random>`): a proof may only claim what its own
        // challenge saw, so a proof with a stale or borrowed nonce cannot
        // drag the fence backwards.
        var nonceParts = proofClaims.Nonce.Split('.', 3);
        if (nonceParts.Length != 3
            || !long.TryParse(nonceParts[0], out var nonceEpoch) || nonceEpoch != subject.Epoch
            || !long.TryParse(nonceParts[1], out var nonceSeq) || proofClaims.Seq != nonceSeq)
        {
            return await DeniedAsync(claims, ProofFailure.Nonce, "nonce does not carry the state it was minted against", subject, ct, resource);
        }

        if (!await store.ConsumeChallengeAsync(proofClaims.Issuer, proofClaims.Nonce, ct))
        {
            return await DeniedAsync(claims, ProofFailure.Nonce, "nonce absent, expired or already used", subject, ct, resource);
        }

        if (!await store.RecordProofJtiAsync(
                proofClaims.Issuer, proofClaims.Jti, now.AddSeconds(options.SessionTokenLifetimeSeconds + options.EvidenceClockSkewSeconds), ct))
        {
            return await DeniedAsync(claims, ProofFailure.Replayed, "jti already seen", subject, ct, resource);
        }

        if (proofClaims.Seq < subject.Seq)
        {
            await audit.WriteAsync(new AuditRecord(
                null, "link.replay-suspect", resource, "deny", "seq-below-fence",
                null, null,
                JsonSerializer.Serialize(new { endpointId = proofClaims.Issuer, claimedSeq = proofClaims.Seq, storedSeq = subject.Seq })), ct);
            return await DeniedAsync(claims, ProofFailure.ReplaySuspect, "seq below the stored fence", subject, ct, resource);
        }

        if (!await store.AdvanceSeqAsync(proofClaims.Issuer, proofClaims.Seq, ct))
        {
            return await DeniedAsync(claims, ProofFailure.ReplaySuspect, "fence moved under the proof", subject, ct, resource);
        }

        await audit.WriteAsync(new AuditRecord(
            null, "link.proof-verified", resource, "allow", null, null, null,
            JsonSerializer.Serialize(new { endpointId = proofClaims.Issuer, keyId = proofClaims.Sub, seq = proofClaims.Seq, epoch = proofClaims.Epoch })), ct);

        return new ProofVerdict(true, default, "verified", proofClaims, subject);
    }

    /// <summary>Mint the short-lived session JWT a verified proof buys (audits
    /// <c>link.session-issued</c> with fingerprint-only detail).</summary>
    public async Task<SessionMint> MintSessionAsync(ProofSubject subject, LinkProofClaims claims, CancellationToken ct, string resource = "/api/link/v1/proof")
    {
        var key = await sessionKeys.CurrentAsync(ct);
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(options.SessionTokenLifetimeSeconds);
        var authSource = subject.CredentialClass == "oidc" ? "oidc" : "key";
        var token = LinkSessionJwt.Mint(key, new SessionClaims(
            subject.EndpointId, subject.TeamSlug, subject.Role, subject.TrustMode, authSource,
            subject.Epoch, claims.Seq, expiresAt));
        await audit.WriteAsync(new AuditRecord(
            null, "link.session-issued", resource, "allow", null, null, null,
            JsonSerializer.Serialize(new
            {
                endpointId = subject.EndpointId,
                keyId = claims.Sub,
                authSource,
                seq = claims.Seq,
                epoch = subject.Epoch,
                expiresAt,
            })), ct);
        return new SessionMint(token, expiresAt, claims.Seq, key.Kid);
    }

    /// <summary>Hand a minted session to a caller that verified a proof (audits
    /// <c>link.session-issued</c>) for a class-oidc principal (issuer trust, no key).</summary>
    public async Task<SessionMint> MintWorkloadSessionAsync(WorkloadSubjectLink link, CancellationToken ct, string resource = "/api/link/v1/proof")
    {
        var key = await sessionKeys.CurrentAsync(ct);
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(options.SessionTokenLifetimeSeconds);
        var token = LinkSessionJwt.Mint(key, new SessionClaims(
            link.EndpointId, link.TeamSlug, link.Role, link.TrustMode, "oidc",
            link.Epoch, link.Seq, expiresAt));
        await audit.WriteAsync(new AuditRecord(
            null, "link.session-issued", resource, "allow", null, null, null,
            JsonSerializer.Serialize(new
            {
                endpointId = link.EndpointId,
                authSource = "oidc",
                seq = link.Seq,
                epoch = link.Epoch,
                expiresAt,
            })), ct);
        return new SessionMint(token, expiresAt, link.Seq, key.Kid);
    }

    /// <summary>
    /// The channel URI in comparable form: a socket scheme is folded to the
    /// transport it rides (ws→http, wss→https), so an endpoint may name its
    /// channel either as it dials it or as the server sees it. Nothing else is
    /// normalized — path, host and port must match exactly, which is what makes
    /// a proof unusable on a different hub or vhost.
    /// </summary>
    internal static string ChannelUri(string htu) =>
        htu switch
        {
            _ when htu.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) => "http://" + htu[5..],
            _ when htu.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) => "https://" + htu[6..],
            _ => htu,
        };

    /// <summary>Build the audit + verdict for a denial (link.proof-failed, fingerprint-only detail).</summary>
    private async Task<ProofVerdict> DeniedAsync(
        LinkProofClaims? claims, ProofFailure failure, string reason, ProofSubject? subject, CancellationToken ct,
        string resource = "/api/link/v1/proof")
    {
        logger.LogInformation(
            "link proof failed endpoint={EndpointId} failure={Failure} reason={Reason}",
            claims?.Issuer ?? "<unparsed>", failure, reason);
        await audit.WriteAsync(new AuditRecord(
            null, "link.proof-failed", resource, "deny", failure.ToString().ToLowerInvariant(), null, null,
            JsonSerializer.Serialize(new
            {
                endpointId = claims?.Issuer,
                keyId = claims?.Sub,
                failure = failure.ToString().ToLowerInvariant(),
                team = subject?.TeamSlug,
            })), ct);
        return new ProofVerdict(false, failure, reason, claims, subject);
    }
}
