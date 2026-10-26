using System.Security.Cryptography;
using System.Text;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>Successful enrollment (the wire response minus nothing: token included).</summary>
/// <param name="EnrollmentId">Enrollment record identity.</param>
/// <param name="EndpointId">Logical endpoint.</param>
/// <param name="TeamId">Bound team.</param>
/// <param name="TrustMode">Declared trust mode.</param>
/// <param name="LinkToken">Freshly minted opaque bearer token (empty on a role-mismatch signal).</param>
/// <param name="ExpiresAt">Token expiry (UTC).</param>
public sealed record EnrollmentResult(Guid EnrollmentId, string EndpointId, Guid TeamId, string TrustMode, string LinkToken, DateTime ExpiresAt)
{
    /// <summary>True when a rotation tried to change the endpoint's immutable role (caller answers 403).</summary>
    public bool RoleMismatch { get; init; }
}

/// <summary>
/// Enrollment + link-token store (appendix §4): validates the bootstrap
/// enrollment secret, upserts by endpoint_id rotating the token (an old token
/// stops validating immediately — the §26.1 item-2 invalidation lever), and
/// resolves bearer tokens for <see cref="ILinkTokenValidator"/>.
/// </summary>
public sealed class LinkEnrollmentStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    LinkEnrollmentSecret secret,
    LinkOptions options,
    TimeProvider timeProvider,
    ILogger<LinkEnrollmentStore> logger)
{
    private static readonly string[] TrustModes = ["shared-runtime", "separate-process-same-host", "isolated-executor"];

    /// <summary>Roles an enrollment may carry; a `gw.`-prefixed endpoint requires gateway.</summary>
    public static readonly string[] Roles = ["gateway", "child"];

    /// <summary>
    /// Enroll (or rotate): mode (a) requires the request secret to match
    /// <c>Link:EnrollmentToken</c>; the endpoint binds to a roster-present team
    /// (seeded from <c>Link:Teams</c> — no on-demand default). Role/team are
    /// immutable across rotation; a role change needs re-enrollment after revoke.
    /// </summary>
    /// <param name="endpointId">Logical endpoint.</param>
    /// <param name="instanceId">Process instance (audit only).</param>
    /// <param name="trustMode">Declared trust mode.</param>
    /// <param name="requestSecret">Mode-(a) bootstrap secret; ignored when <paramref name="workload"/> is set.</param>
    /// <param name="teamSlug">Team the endpoint binds to (must exist in the roster).</param>
    /// <param name="role">gateway | child (validated by the caller against <see cref="Roles"/>).</param>
    /// <param name="acceptsDelegatedClaims">Delegation acceptance (mode (b) binding; false for mode (a)).</param>
    /// <param name="workload">Mode-(b) workload identity (issuer+subject); null for mode (a).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<EnrollmentResult?> EnrollAsync(
        string endpointId, string instanceId, string trustMode, string? requestSecret,
        string teamSlug, string role, bool acceptsDelegatedClaims,
        (string Issuer, string Subject)? workload, CancellationToken ct)
    {
        if (workload is null && !secret.Matches(requestSecret))
        {
            return null;
        }

        if (!TrustModes.Contains(trustMode, StringComparer.Ordinal))
        {
            throw new ArgumentException($"unknown trustMode '{trustMode}'", nameof(trustMode));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enrollmentId = Guid.NewGuid();
        var token = NewToken();
        var expiresAt = now.AddHours(options.TokenLifetimeHours);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // No on-demand default team: the roster (Link:Teams, seeded at startup) is truth.
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == teamSlug, ct);
        if (team is null)
        {
            return null;
        }

        var existing = await db.LinkEnrollments.FirstOrDefaultAsync(e => e.EndpointId == endpointId, ct);
        if (existing is null)
        {
            db.LinkEnrollments.Add(new LinkEnrollmentEntity
            {
                EnrollmentId = enrollmentId,
                EndpointId = endpointId,
                TeamId = team.TeamId,
                TokenSha256 = Sha256(token),
                TrustMode = trustMode,
                EnrolledAt = now,
                ExpiresAt = expiresAt,
                Role = role,
                AcceptsDelegatedClaims = acceptsDelegatedClaims,
                WorkloadIssuer = workload?.Issuer,
                WorkloadSubject = workload?.Subject,
            });
        }
        else
        {
            if (!string.Equals(existing.Role, role, StringComparison.Ordinal))
            {
                // Rotation may not re-role: signal the mismatch, rotate nothing.
                return new EnrollmentResult(existing.EnrollmentId, endpointId, existing.TeamId, trustMode, string.Empty, existing.ExpiresAt)
                { RoleMismatch = true };
            }

            existing.TokenSha256 = Sha256(token); // rotation: the old token stops validating immediately
            existing.TrustMode = trustMode;
            existing.RevokedAt = null;
            existing.EnrolledAt = now;
            existing.ExpiresAt = expiresAt;
            existing.AcceptsDelegatedClaims = acceptsDelegatedClaims;
            existing.WorkloadIssuer = workload?.Issuer;
            existing.WorkloadSubject = workload?.Subject;
        }

        await db.SaveChangesAsync(ct);

        // appendix §4: trustMode MUST be logged at enrollment (and every Hello).
        logger.LogInformation(
            "link endpoint enrolled endpoint={EndpointId} instance={InstanceId} trustMode={TrustMode} team={TeamId} role={Role}",
            endpointId, instanceId, trustMode, team.TeamId, role);

        return new EnrollmentResult(enrollmentId, endpointId, team.TeamId, trustMode, token, expiresAt);
    }

    /// <summary>True when the slug is in the team roster (facilitator.teams).</summary>
    public async Task<bool> TeamExistsAsync(string slug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Teams.AsNoTracking().AnyAsync(t => t.Slug == slug, ct);
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

    /// <summary>Revoke an endpoint's credentials (admin surface; goodbye never revokes).</summary>
    public async Task RevokeAsync(string endpointId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.LinkEnrollments
            .Where(e => e.EndpointId == endpointId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.RevokedAt, timeProvider.GetUtcNow().UtcDateTime), ct);
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    internal static string Sha256(string token) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Store-backed <see cref="ILinkTokenValidator"/>: SHA-256 the bearer token and
/// look it up in <c>link_enrollments</c> (active + not expired + not revoked),
/// joining <c>teams</c> so the principal carries the team slug and role.
/// </summary>
public sealed class StoreLinkTokenValidator(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider) : ILinkTokenValidator
{
    /// <inheritdoc />
    public async Task<LinkPrincipal?> ValidateAsync(string token, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var hash = LinkEnrollmentStore.Sha256(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.TokenSha256 == hash && e.RevokedAt == null && e.ExpiresAt > now)
            .Join(db.Teams.AsNoTracking(), e => e.TeamId, t => t.TeamId,
                (e, t) => new { e.EndpointId, e.TeamId, t.Slug, e.TrustMode, e.Role })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new LinkPrincipal(row.EndpointId, row.TeamId, row.Slug, row.TrustMode, row.Role);
    }
}
