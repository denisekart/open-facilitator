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
/// <param name="LinkToken">Freshly minted opaque bearer token.</param>
/// <param name="ExpiresAt">Token expiry (UTC).</param>
public sealed record EnrollmentResult(Guid EnrollmentId, string EndpointId, Guid TeamId, string TrustMode, string LinkToken, DateTime ExpiresAt);

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

    /// <summary>
    /// Enroll (or rotate): the request secret must match
    /// <c>Link:EnrollmentToken</c>; the endpoint binds to the <c>default</c>
    /// team, seeded on demand when the table is still empty.
    /// </summary>
    public async Task<EnrollmentResult?> EnrollAsync(
        string endpointId, string instanceId, string trustMode, string? requestSecret, CancellationToken ct)
    {
        if (!secret.Matches(requestSecret))
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

        // Plan 11: replace default-team seeding (proper team provisioning).
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == "default", ct);
        if (team is null)
        {
            team = new TeamEntity { TeamId = Guid.NewGuid(), Slug = "default", CreatedAt = now };
            db.Teams.Add(team);
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
            });
        }
        else
        {
            existing.TokenSha256 = Sha256(token); // rotation: the old token stops validating immediately
            existing.TrustMode = trustMode;
            existing.RevokedAt = null;
            existing.EnrolledAt = now;
            existing.ExpiresAt = expiresAt;
        }

        await db.SaveChangesAsync(ct);

        // appendix §4: trustMode MUST be logged at enrollment (and every Hello).
        logger.LogInformation(
            "link endpoint enrolled endpoint={EndpointId} instance={InstanceId} trustMode={TrustMode} team={TeamId}",
            endpointId, instanceId, trustMode, team.TeamId);

        return new EnrollmentResult(enrollmentId, endpointId, team.TeamId, trustMode, token, expiresAt);
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
/// look it up in <c>link_enrollments</c> (active + not expired + not revoked).
/// Plan 07 replaces this with OIDC validation + <c>principal_mappings</c>.
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
            .Select(e => new { e.EndpointId, e.TeamId, e.TrustMode })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new LinkPrincipal(row.EndpointId, row.TeamId, row.TrustMode);
    }
}
