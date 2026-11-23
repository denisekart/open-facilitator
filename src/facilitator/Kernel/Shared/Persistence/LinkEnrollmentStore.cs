using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>Successful enrollment: identity, not secrets — the wire response is
/// the key fingerprint and the replay fence, nothing to steal.</summary>
/// <param name="EnrollmentId">Enrollment record identity.</param>
/// <param name="EndpointId">Logical endpoint.</param>
/// <param name="TeamId">Bound team.</param>
/// <param name="TrustMode">Declared trust mode.</param>
/// <param name="EndpointKeyId"><c>sha256:</c> fingerprint of the registered identity
/// (mode (b): the deterministic fingerprint of the workload identity).</param>
/// <param name="Seq">Replay fence the endpoint starts from (fresh enrollments: 0).</param>
public sealed record EnrollmentResult(Guid EnrollmentId, string EndpointId, Guid TeamId, string TrustMode, string EndpointKeyId, long Seq)
{
    /// <summary>True when a rotation tried to change the endpoint's immutable role (caller answers 403).</summary>
    public bool RoleMismatch { get; init; }
}

/// <summary>Outcome of <see cref="RotateKeyAsync"/>.</summary>
public enum RotateKeyOutcome
{
    /// <summary>Swapped: new key active, old key retired, epoch untouched.</summary>
    Rotated,

    /// <summary>Swapped from a key retired inside the grace window — the grace
    /// authorized this next rotation and nothing else.</summary>
    GraceConsumed,

    /// <summary><c>oldKeyId</c> is not a key of this enrollment.</summary>
    UnknownKey,

    /// <summary>The named key retired outside <c>Link:RotateGraceMinutes</c>: revoke and rejoin.</summary>
    GraceExpired,

    /// <summary>Claimed seq below the stored fence.</summary>
    ReplaySuspect,

    /// <summary>Enrollment revoked (or gone).</summary>
    Revoked,
}

/// <summary>
/// Enrollment + endpoint-key store: validates the bootstrap enrollment code,
/// upserts by endpoint_id and swaps the key list (the previous key stops
/// validating immediately — the invalidation lever), fences proofs against the
/// monotonic <c>seq</c>, and bumps the revocation <c>epoch</c> so a revoke kills
/// the endpoint, every key, every proof and every session without a session table.
/// </summary>
public sealed class LinkEnrollmentStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    LinkEnrollmentSecret secret,
    AuditWriter audit,
    TimeProvider timeProvider,
    ILogger<LinkEnrollmentStore> logger)
{
    private static readonly string[] TrustModes = ["shared-runtime", "separate-process-same-host", "isolated-executor"];

    /// <summary>Roles an enrollment may carry; a `gw.`-prefixed endpoint requires gateway.</summary>
    public static readonly string[] Roles = ["gateway", "child"];

    /// <summary>Credential classes (closed set): a key is proven, an issuer is trusted.</summary>
    public static readonly string[] CredentialClasses = ["oidc", "key"];

    /// <summary>
    /// Enroll (or re-enroll): mode (a, class <c>key</c>) requires the request
    /// secret to match <c>Link:EnrollmentToken</c> (or a minted code, upstream)
    /// AND a parseable Ed25519 public key, which becomes the endpoint's
    /// identity; the previous key retires on the spot. Mode (b, class
    /// <c>oidc</c>) writes no key row — identity stays the issuer trust. The
    /// endpoint binds to a roster-present team (seeded from <c>Link:Teams</c> —
    /// no on-demand default). Role/team are immutable across rotation; a role
    /// change needs re-enrollment after revoke.
    /// </summary>
    /// <param name="endpointId">Logical endpoint.</param>
    /// <param name="instanceId">Process instance (audit only).</param>
    /// <param name="trustMode">Declared trust mode.</param>
    /// <param name="requestSecret">Mode-(a) bootstrap code; ignored when <paramref name="workload"/> is set.</param>
    /// <param name="teamSlug">Team the endpoint binds to (must exist in the roster).</param>
    /// <param name="role">gateway | child (validated by the caller against <see cref="Roles"/>).</param>
    /// <param name="acceptsDelegatedClaims">Delegation acceptance (mode (b) binding; false for mode (a)).</param>
    /// <param name="workload">Mode-(b) workload identity (issuer+subject); null for mode (a).</param>
    /// <param name="publicKeyBase64Url">Mode-(a) raw Ed25519 public key (base64url); null for mode (b).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<EnrollmentResult?> EnrollAsync(
        string endpointId, string instanceId, string trustMode, string? requestSecret,
        string teamSlug, string role, bool acceptsDelegatedClaims,
        (string Issuer, string Subject)? workload, string? publicKeyBase64Url, CancellationToken ct)
    {
        if (workload is null && !secret.Matches(requestSecret))
        {
            return null;
        }

        if (!TrustModes.Contains(trustMode, StringComparer.Ordinal))
        {
            throw new ArgumentException($"unknown trustMode '{trustMode}'", nameof(trustMode));
        }

        var credentialClass = workload is null ? "key" : "oidc";
        string keyId;
        byte[]? rawKey = null;
        if (credentialClass == "key")
        {
            if (!LinkKeys.TryParsePublicKey(publicKeyBase64Url, out rawKey))
            {
                throw new ArgumentException("publicKey must be one raw Ed25519 key in base64url (43 or 44 characters)", nameof(publicKeyBase64Url));
            }

            keyId = LinkKeys.KeyId(rawKey);
        }
        else
        {
            // Mode (b): no key row — the fingerprint fingerprints the issuer trust
            // so the wire stays one shape for both credential classes.
            keyId = LinkKeys.KeyId(LinkKeys.Utf8($"oidc:{workload!.Value.Issuer}|{workload.Value.Subject}"));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enrollmentId = Guid.NewGuid();
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // No on-demand default team: the roster (Link:Teams, seeded at startup) is truth.
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == teamSlug, ct);
        if (team is null)
        {
            return null;
        }

        var existing = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EndpointId == endpointId, ct);
        long seq;
        if (existing is null)
        {
            seq = 0;
            var entity = new LinkEnrollmentEntity
            {
                EnrollmentId = enrollmentId,
                EndpointId = endpointId,
                TeamId = team.TeamId,
                CredentialClass = credentialClass,
                Epoch = 0,
                Seq = 0,
                TrustMode = trustMode,
                EnrolledAt = now,
                ExpiresAt = SessionlessExpiry(now),
                Role = role,
                AcceptsDelegatedClaims = acceptsDelegatedClaims,
                WorkloadIssuer = workload?.Issuer,
                WorkloadSubject = workload?.Subject,
            };
            db.LinkEnrollments.Add(entity);
            if (credentialClass == "key")
            {
                db.LinkEndpointKeys.Add(new LinkEndpointKeyEntity
                {
                    EnrollmentId = enrollmentId,
                    KeyId = keyId,
                    PublicKey = publicKeyBase64Url!,
                    KeyAlgorithm = "ed25519",
                    State = "active",
                    RegisteredAt = now,
                });
            }
        }
        else
        {
            if (!string.Equals(existing.Role, role, StringComparison.Ordinal))
            {
                // Rotation may not re-role: signal the mismatch, rotate nothing.
                return new EnrollmentResult(existing.EnrollmentId, endpointId, existing.TeamId, trustMode, keyId, existing.Seq)
                { RoleMismatch = true };
            }

            enrollmentId = existing.EnrollmentId;
            existing.CredentialClass = credentialClass;
            existing.TrustMode = trustMode;
            existing.RevokedAt = null;
            existing.EnrolledAt = now;
            existing.ExpiresAt = SessionlessExpiry(now);
            existing.AcceptsDelegatedClaims = acceptsDelegatedClaims;
            existing.WorkloadIssuer = workload?.Issuer;
            existing.WorkloadSubject = workload?.Subject;
            if (credentialClass == "key")
            {
                // The rotate-on-re-enroll law: a re-enroll restarts the fence —
                // the key swap is the reset.
                seq = 0;
                existing.Seq = 0;
            }
            else
            {
                // A class-oidc re-enroll rotates no key: the fence keeps
                // counting, so a session minted BEFORE this re-enroll stays
                // fenced out (a re-arm would let the stale bearer replay).
                seq = existing.Seq + 1;
                existing.Seq = seq;
            }
            if (credentialClass == "key")
            {
                // The rotate-on-re-enroll law: a DIFFERENT presented key retires the
                // active one, so a proof by it stops validating immediately (epoch
                // untouched — this is a key rotation, not a revocation). Presenting
                // the SAME key again is a restart, not a rotation: the key stays
                // active, because re-adding it would violate the one-active-key
                // index and silently invalidate the very identity that rebooted.
                var presented = await db.LinkEndpointKeys.FirstOrDefaultAsync(
                    k => k.EnrollmentId == existing.EnrollmentId && k.KeyId == keyId, ct);
                if (presented is null)
                {
                    await db.LinkEndpointKeys
                        .Where(k => k.EnrollmentId == existing.EnrollmentId && k.State == "active")
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(k => k.State, "retired")
                            .SetProperty(k => k.RetiredAt, now),
                            ct);
                    db.LinkEndpointKeys.Add(new LinkEndpointKeyEntity
                    {
                        EnrollmentId = existing.EnrollmentId,
                        KeyId = keyId,
                        PublicKey = publicKeyBase64Url!,
                        KeyAlgorithm = "ed25519",
                        State = "active",
                        RegisteredAt = now,
                    });
                }
                else
                {
                    presented.State = "active";
                    presented.RetiredAt = null;
                }
            }
        }

        audit.Append(db, new AuditRecord(
            null, "link.enrollment-issued", $"/api/link/v1/enroll/{endpointId}", "allow", null, null, null,
            JsonSerializer.Serialize(new { endpointId, instanceId, trustMode, role, credentialClass, endpointKeyId = keyId })));
        await db.SaveChangesAsync(ct);

        // TrustMode MUST be logged at enrollment (and every Hello).
        logger.LogInformation(
            "link endpoint enrolled endpoint={EndpointId} instance={InstanceId} trustMode={TrustMode} team={TeamId} role={Role} key={KeyId}",
            endpointId, instanceId, trustMode, team.TeamId, role, keyId);

        return new EnrollmentResult(enrollmentId, endpointId, team.TeamId, trustMode, keyId, seq);
    }

    /// <summary>K9: bind an enrollment to a contributor (the workload speaks AS
    /// that named actor). Several enrollments may share one contributor id.</summary>
    public async Task BindContributorAsync(Guid enrollmentId, string handle, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var contributor = await db.Contributors.FirstOrDefaultAsync(c => c.Handle == handle, ct);
        if (contributor is null)
        {
            throw new ArgumentException($"contributor '{handle}' is not registered");
        }

        var enrollment = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId, ct)
            ?? throw new ArgumentException("enrollment not found");
        if (enrollment.TeamId != contributor.TeamId)
        {
            throw new ArgumentException($"contributor '{handle}' belongs to another team");
        }

        enrollment.ContributorId = contributor.ContributorId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when the slug is in the team roster (facilitator.teams).</summary>
    public async Task<bool> TeamExistsAsync(string slug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Teams.AsNoTracking().AnyAsync(t => t.Slug == slug, ct);
    }

    /// <summary>True when the endpoint's enrollment accepts oidc: delegated evidence claims.</summary>
    public async Task<bool> AcceptsDelegatedClaimsAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.EndpointId == endpointId)
            .Select(e => e.AcceptsDelegatedClaims)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Store an advertised capability manifest (+ SHA-256 digest) for an endpoint.</summary>
    public async Task StoreCapabilitiesAsync(string endpointId, string manifestJson, string digest, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var enrollment = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EndpointId == endpointId, ct)
            ?? throw new InvalidOperationException($"no enrollment for '{endpointId}'");
        enrollment.CapabilityManifest = manifestJson;
        enrollment.CapabilitiesDigest = digest;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stored manifest jsonb + team slug (null when no enrollment exists).</summary>
    public async Task<(string? Manifest, string TeamSlug)?> GetCapabilitiesAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.EndpointId == endpointId)
            .Join(db.Teams.AsNoTracking(), e => e.TeamId, t => t.TeamId, (e, t) => new { e.CapabilityManifest, t.Slug })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.CapabilityManifest, row.Slug);
    }

    /// <summary>
    /// Revoke an endpoint's credentials (admin surface; goodbye never revokes).
    /// This single statement is the law: the endpoint, every key, every proof
    /// and every session die — the epoch bump is what kills a live session
    /// inside its own token lifetime without a session table. The NOTIFY is a
    /// hint (freshness); the validator's cache TTL is the correctness floor.
    /// False when nothing was on file (the DELETE admin route answers 404) —
    /// and a row already revoked is "nothing": re-revoking must not re-bump
    /// the epoch of a credential that is already dead.
    /// </summary>
    public async Task<bool> RevokeAsync(string endpointId, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await db.LinkEnrollments
            .Where(e => e.EndpointId == endpointId && e.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.RevokedAt, now)
                .SetProperty(e => e.Epoch, e => e.Epoch + 1),
                ct);
        if (rows == 0)
        {
            return false;
        }

        var enrollmentIds = await db.LinkEnrollments
            .Where(e => e.EndpointId == endpointId)
            .Select(e => e.EnrollmentId)
            .ToListAsync(ct);
        await db.LinkEndpointKeys
            .Where(k => enrollmentIds.Contains(k.EnrollmentId) && k.State == "active")
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.State, "retired")
                .SetProperty(k => k.RetiredAt, now),
                ct);

        audit.Append(db, new AuditRecord(
            actor, "link.endpoint-revoked", $"/api/link/v1/enroll/{endpointId}", "allow", null, null, null,
            JsonSerializer.Serialize(new { endpointId, revokedAt = now })));
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyRevocationAsync(db, endpointId, ct);
        return true;
    }

    /// <summary>
    /// Swap an endpoint's active key under proof by both keys (see the rotate
    /// endpoint). <paramref name="oldKeyId"/> may name a key retired less than
    /// <paramref name="grace"/> ago — the grace authorizes this next rotation,
    /// never a first key, and only the specific retired key it names. The
    /// fence moves to <paramref name="expectedSeq"/>; <c>Epoch</c> is untouched.
    /// </summary>
    public async Task<RotateKeyOutcome> RotateKeyAsync(
        string endpointId, string oldKeyId, string newKeyId, string newPublicKeyBase64Url,
        long expectedSeq, TimeSpan grace, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enrollment = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EndpointId == endpointId, ct);
        if (enrollment is null || enrollment.RevokedAt is not null)
        {
            return RotateKeyOutcome.Revoked;
        }

        if (expectedSeq < enrollment.Seq)
        {
            return RotateKeyOutcome.ReplaySuspect;
        }

        var old = await db.LinkEndpointKeys.FirstOrDefaultAsync(
            k => k.EnrollmentId == enrollment.EnrollmentId && k.KeyId == oldKeyId, ct);
        if (old is null)
        {
            return RotateKeyOutcome.UnknownKey;
        }

        var graceConsumed = false;
        if (old.State == "retired")
        {
            if (old.RetiredAt is null || now - old.RetiredAt.Value > grace)
            {
                return RotateKeyOutcome.GraceExpired;
            }

            graceConsumed = true;
        }

        await db.LinkEndpointKeys
            .Where(k => k.EnrollmentId == enrollment.EnrollmentId && k.State == "active" && k.KeyId != newKeyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.State, "retired")
                .SetProperty(k => k.RetiredAt, now),
                ct);

        if (old.State == "active")
        {
            old.State = "retired";
            old.RetiredAt = now;
        }

        if (!await db.LinkEndpointKeys.AnyAsync(k => k.EnrollmentId == enrollment.EnrollmentId && k.KeyId == newKeyId, ct))
        {
            db.LinkEndpointKeys.Add(new LinkEndpointKeyEntity
            {
                EnrollmentId = enrollment.EnrollmentId,
                KeyId = newKeyId,
                PublicKey = newPublicKeyBase64Url,
                KeyAlgorithm = "ed25519",
                State = "active",
                RegisteredAt = now,
            });
        }

        enrollment.Seq = expectedSeq;
        var detail = JsonSerializer.Serialize(new
        {
            endpointId,
            oldKeyId,
            newKeyId,
            seq = expectedSeq,
            epoch = enrollment.Epoch,
            graceConsumed,
        });
        if (graceConsumed)
        {
            audit.Append(db, new AuditRecord(
                actor, "link.rotate-grace-consumed", $"/api/link/v1/enroll/{endpointId}/rotate", "allow", null, null, null, detail));
        }

        audit.Append(db, new AuditRecord(
            actor, "link.key-rotated", $"/api/link/v1/enroll/{endpointId}/rotate", "allow", null, null, null, detail));
        await db.SaveChangesAsync(ct);
        return graceConsumed ? RotateKeyOutcome.GraceConsumed : RotateKeyOutcome.Rotated;
    }

    /// <summary>
    /// Move the replay fence forward: <c>false</c> when the claimed seq is below
    /// the stored one (caller audits <c>link.replay-suspect</c>). Shared by
    /// proof verification and session-JWT mint.
    /// </summary>
    public async Task<bool> AdvanceSeqAsync(string endpointId, long claimedSeq, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var enrollment = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EndpointId == endpointId, ct);
        if (enrollment is null || claimedSeq < enrollment.Seq)
        {
            return false;
        }

        if (claimedSeq > enrollment.Seq)
        {
            enrollment.Seq = claimedSeq;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>Issue a single-use proof nonce for an endpoint (unknown endpoint ⇒ false).</summary>
    public async Task<bool> IssueChallengeAsync(string endpointId, string nonce, DateTime expiresAt, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.LinkEnrollments.AnyAsync(e => e.EndpointId == endpointId && e.RevokedAt == null, ct))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.LinkChallenges.Add(new LinkChallengeEntity
        {
            Nonce = nonce,
            EndpointId = endpointId,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            Consumed = false,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Consume a fresh, unexpired nonce for this endpoint (atomic: exactly-once).</summary>
    public async Task<bool> ConsumeChallengeAsync(string endpointId, string nonce, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await db.LinkChallenges
            .Where(c => c.Nonce == nonce && c.EndpointId == endpointId && !c.Consumed && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Consumed, true), ct);
        return rows == 1;
    }

    /// <summary>Record a proof <c>jti</c> as seen (same table, <c>link-proof:</c> prefix —
    /// one replay mechanism). False when the jti was already seen.</summary>
    public async Task<bool> RecordProofJtiAsync(string endpointId, string jti, DateTime expiresAt, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.LinkChallenges.Add(new LinkChallengeEntity
        {
            Nonce = "link-proof:" + jti,
            EndpointId = endpointId,
            IssuedAt = timeProvider.GetUtcNow().UtcDateTime,
            ExpiresAt = expiresAt,
            Consumed = true,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verification read-model for a proof: the enrollment row (epoch, fence,
    /// revocation) joined to the team slug and to the claimed key's state.
    /// Null when the endpoint or the key does not exist at all.
    /// </summary>
    public async Task<ProofSubject?> GetProofSubjectAsync(string endpointId, string keyId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.EndpointId == endpointId)
            .Join(db.Teams.AsNoTracking(), e => e.TeamId, t => t.TeamId, (e, t) => new
            {
                e.EndpointId,
                e.EnrollmentId,
                e.TeamId,
                Slug = t.Slug,
                e.TrustMode,
                e.Role,
                e.CredentialClass,
                e.Epoch,
                e.Seq,
                e.RevokedAt,
                e.ExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var key = await db.LinkEndpointKeys.AsNoTracking()
            .Where(k => k.EnrollmentId == row.EnrollmentId && k.KeyId == keyId)
            .Select(k => new { k.PublicKey, k.State, k.RetiredAt })
            .FirstOrDefaultAsync(ct);
        return new ProofSubject(
            row.EndpointId, row.TeamId, row.Slug, row.TrustMode, row.Role, row.CredentialClass,
            row.Epoch, row.Seq, row.RevokedAt is not null || row.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime,
            key?.PublicKey, key?.State, key?.RetiredAt);
    }

    /// <summary>The active key of an enrollment (null when class <c>oidc</c> or revoked).</summary>
    public async Task<(string KeyId, string PublicKey)?> GetActiveKeyAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkEndpointKeys.AsNoTracking()
            .Where(k => k.State == "active" && db.LinkEnrollments.Any(e => e.EnrollmentId == k.EnrollmentId && e.EndpointId == endpointId))
            .Select(k => new { k.KeyId, k.PublicKey })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.KeyId, row.PublicKey);
    }

    /// <summary>Delete challenge/jti rows past expiry (bounded growth, no new config key).</summary>
    public async Task<int> SweepChallengesAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.LinkChallenges
            .Where(c => c.ExpiresAt < timeProvider.GetUtcNow().UtcDateTime)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>The enrollment row a session JWT needs to be verified against (no second query at proof time).</summary>
    public async Task<SessionSubject?> GetSessionSubjectAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.EndpointId == endpointId)
            .Join(db.Teams.AsNoTracking(), e => e.TeamId, t => t.TeamId, (e, t) => new
            {
                e.TeamId,
                Slug = t.Slug,
                e.TrustMode,
                e.Role,
                e.CredentialClass,
                e.Epoch,
                e.Seq,
                e.RevokedAt,
                e.ExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new SessionSubject(row.TeamId, row.Slug, row.TrustMode, row.Role, row.CredentialClass, row.Epoch, row.Seq,
                row.RevokedAt is not null || row.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>Mode (b): enrollment bound to a workload issuer+subject (null when none).</summary>
    public async Task<WorkloadSubjectLink?> FindWorkloadEnrollmentAsync(string issuer, string subject, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.WorkloadIssuer == issuer && e.WorkloadSubject == subject)
            .Join(db.Teams.AsNoTracking(), e => e.TeamId, t => t.TeamId, (e, t) => new
            {
                e.EndpointId,
                e.TeamId,
                Slug = t.Slug,
                e.TrustMode,
                e.Role,
                e.Epoch,
                e.Seq,
                e.RevokedAt,
                e.ExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new WorkloadSubjectLink(row.EndpointId, row.TeamId, row.Slug, row.TrustMode, row.Role,
                row.Epoch, row.Seq, row.RevokedAt is not null || row.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>Enrollment rows for a batch of endpoints (proof-time freshness checks).</summary>
    public async Task<IReadOnlyDictionary<string, (long Epoch, long Seq, bool Dead)>> PeekFencesAsync(
        IReadOnlyCollection<string> endpointIds, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await db.LinkEnrollments.AsNoTracking()
            .Where(e => endpointIds.Contains(e.EndpointId))
            .Select(e => new { e.EndpointId, e.Epoch, e.Seq, Dead = e.RevokedAt != null || e.ExpiresAt <= now })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.EndpointId, r => (r.Epoch, r.Seq, r.Dead));
    }

    /// <summary>Enrollments carry no bearer of their own anymore; the row just
    /// has to outlive the reference period a session can claim (one day,
    /// refreshed on every enroll) — the session JWT owns short-lived truth.</summary>
    private static DateTime SessionlessExpiry(DateTime now) => now.AddDays(1);

    internal static string Sha256(string token) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Proof verification read-model (see <see cref="LinkEnrollmentStore.GetProofSubjectAsync"/>).</summary>
public sealed record ProofSubject(
    string EndpointId,
    Guid TeamId,
    string TeamSlug,
    string TrustMode,
    string Role,
    string CredentialClass,
    long Epoch,
    long Seq,
    bool Dead,
    string? ClaimedKeyPublicKey,
    string? ClaimedKeyState,
    DateTime? ClaimedKeyRetiredAt);

/// <summary>Session-JWT verification read-model (see <see cref="LinkEnrollmentStore.GetSessionSubjectAsync"/>).</summary>
public sealed record SessionSubject(
    Guid TeamId,
    string TeamSlug,
    string TrustMode,
    string Role,
    string CredentialClass,
    long Epoch,
    long Seq,
    bool Dead);

/// <summary>Mode-(b) enrollment lookup result (see <see cref="LinkEnrollmentStore.FindWorkloadEnrollmentAsync"/>).</summary>
public sealed record WorkloadSubjectLink(
    string EndpointId,
    Guid TeamId,
    string TeamSlug,
    string TrustMode,
    string Role,
    long Epoch,
    long Seq,
    bool Dead);
