using System.Security.Cryptography;

using Facilitator.Kernel.Shared.Identity;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>A successful mint: the literal is returned once and never stored.</summary>
/// <param name="Id">Token row identity.</param>
/// <param name="Token">The minted literal (<c>et_</c> + 32 random bytes base64url).</param>
/// <param name="ExpiresAt">First-use deadline (UTC); after consumption the row is the endpoint's standing secret.</param>
public sealed record EnrollmentTokenMintResult(Guid Id, string Token, DateTime ExpiresAt);

/// <summary>One <c>enrollment_tokens</c> row as the admin list surfaces it (never the token).</summary>
public sealed record EnrollmentTokenRow(
    Guid Id,
    string Label,
    string Team,
    string Role,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    string? ConsumedEndpointId,
    DateTime? RevokedAt);

/// <summary>Revoke outcomes for <c>DELETE /api/admin/enroll-tokens/{id}</c>.</summary>
public enum RevokeTokenOutcome
{
    /// <summary>Unknown id (404).</summary>
    NotFound,

    /// <summary>Already revoked — idempotent success (204).</summary>
    AlreadyRevoked,

    /// <summary>Revoked now; the caller additionally kills the consumed link enrollment (204).</summary>
    Revoked,
}

/// <summary>Mode-(a) minted-token verdicts for <c>POST /api/link/v1/enroll</c>.</summary>
public enum MintedTokenOutcome
{
    /// <summary>No <c>enrollment_tokens</c> row for SHA-256(token): the static-secret arm decides, unchanged.</summary>
    NotMinted,

    /// <summary>Row present but revoked or expired (401).</summary>
    Invalid,

    /// <summary>Row consumed by a different endpoint (401 — the standing secret is the consuming endpoint's).</summary>
    EndpointMismatch,

    /// <summary>Request role contradicts the token row (403 role-mismatch).</summary>
    RoleMismatch,

    /// <summary>Request team contradicts the token row (403 team-mismatch).</summary>
    TeamMismatch,

    /// <summary>Accepted: enroll with the row's team/role, then <see cref="EnrollmentTokenStore.TryConsumeAsync"/>.</summary>
    Ok,
}

/// <summary>Verdict + the binding the enroll must apply (<see cref="MintedTokenOutcome.Ok"/> only).</summary>
/// <param name="Outcome">The decision.</param>
/// <param name="TokenId">Token row id (Ok only).</param>
/// <param name="WasConsumed">False on first use (the enroll must then mark the row consumed).</param>
/// <param name="Team">Team the enroll binds (row truth, not the request's).</param>
/// <param name="Role">Role the enroll carries (row truth).</param>
public sealed record MintedTokenDecision(MintedTokenOutcome Outcome, Guid? TokenId = null, bool WasConsumed = false, string? Team = null, string? Role = null);

/// <summary>
/// Admin-minted enrollment tokens (facilitator.enrollment_tokens):
/// a mint returns the literal exactly once and stores only its SHA-256
/// (same hashing law as <see cref="LinkEnrollmentStore"/>); the mode-(a)
/// enroll consumes it on first use, after which the literal is that
/// endpoint's standing re-enroll secret until revoke/expiry. Revoke also
/// kills the consumption-linked link enrollment. Token literals are NEVER
/// logged.
/// </summary>
public sealed class EnrollmentTokenStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Mint a token for <paramref name="teamSlug"/>/<paramref name="role"/> (roster checked by the caller).</summary>
    /// <param name="label">Operator label.</param>
    /// <param name="teamSlug">Team roster slug the consuming enroll must name.</param>
    /// <param name="role">gateway | child — the role the consuming enroll must carry.</param>
    /// <param name="ttlMinutes">First-use window (already clamped by the caller).</param>
    /// <param name="createdBy">Actor of the minting principal (audit).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<EnrollmentTokenMintResult> MintAsync(
        string label, string teamSlug, string role, int ttlMinutes, string? createdBy, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var token = NewLiteral();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.EnrollmentTokens.Add(new EnrollmentTokenEntity
        {
            Id = id,
            TokenHash = LinkEnrollmentStore.Sha256(token),
            Label = label,
            Team = teamSlug,
            Role = role,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ttlMinutes),
        });
        audit.Append(db, new AuditRecord(createdBy, "admin.enroll-tokens.mint", "/api/admin/enroll-tokens", "allow", null, null, null,
            System.Text.Json.JsonSerializer.Serialize(new { id, label, team = teamSlug, role, ttlMinutes })));
        await db.SaveChangesAsync(ct);
        return new EnrollmentTokenMintResult(id, token, now.AddMinutes(ttlMinutes));
    }

    /// <summary>Every token row, newest first (never the token or its hash).</summary>
    public async Task<IReadOnlyList<EnrollmentTokenRow>> ListAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.EnrollmentTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new EnrollmentTokenRow(t.Id, t.Label, t.Team, t.Role, t.CreatedAt, t.ExpiresAt,
                t.ConsumedAt, t.ConsumedEndpointId, t.RevokedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Mode-(a) minted-token law: SHA-256 the candidate; a hash miss is
    /// <see cref="MintedTokenOutcome.NotMinted"/> (the static-secret arm stays byte-identical).
    /// A row present ignores the static secret entirely.
    /// </summary>
    /// <param name="candidate">The presented token literal.</param>
    /// <param name="team">Request team (empty when absent — a row match then answers team-mismatch).</param>
    /// <param name="role">Request role (caller-applied default).</param>
    /// <param name="endpointId">Request endpoint (consumed rows may only re-enroll their own endpoint).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MintedTokenDecision> DecideAsync(string candidate, string team, string role, string endpointId, CancellationToken ct)
    {
        var hash = LinkEnrollmentStore.Sha256(candidate);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.EnrollmentTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.Team, t.Role, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.ConsumedAt, t.ConsumedEndpointId })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return new MintedTokenDecision(MintedTokenOutcome.NotMinted);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (row.RevokedAt is not null || row.ExpiresAt <= now)
        {
            return new MintedTokenDecision(MintedTokenOutcome.Invalid, row.Id, false, row.Team, row.Role);
        }

        if (row.ConsumedAt is not null && !string.Equals(row.ConsumedEndpointId, endpointId, StringComparison.Ordinal))
        {
            return new MintedTokenDecision(MintedTokenOutcome.EndpointMismatch, row.Id, false, row.Team, row.Role);
        }

        if (!string.Equals(row.Role, role, StringComparison.Ordinal))
        {
            return new MintedTokenDecision(MintedTokenOutcome.RoleMismatch, row.Id, false, row.Team, row.Role);
        }

        if (!string.Equals(row.Team, team, StringComparison.Ordinal))
        {
            return new MintedTokenDecision(MintedTokenOutcome.TeamMismatch, row.Id, false, row.Team, row.Role);
        }

        return new MintedTokenDecision(MintedTokenOutcome.Ok, row.Id, row.ConsumedAt is not null, row.Team, row.Role);
    }

    /// <summary>
    /// First-use consumption fence: stamps consumed_at/consumed_endpoint_id/
    /// link_enrollment_id only while the row is still unconsumed (concurrent
    /// first-use: exactly one caller wins, the loser re-auths as a foreign
    /// endpoint and answers 401).
    /// </summary>
    /// <param name="id">Token row id.</param>
    /// <param name="endpointId">The consuming endpoint.</param>
    /// <param name="linkEnrollmentId">The enrollment this call minted (revoke kills it via this link).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> TryConsumeAsync(Guid id, string endpointId, Guid linkEnrollmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var affected = await db.EnrollmentTokens
            .Where(t => t.Id == id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ConsumedAt, timeProvider.GetUtcNow().UtcDateTime)
                .SetProperty(t => t.ConsumedEndpointId, endpointId)
                .SetProperty(t => t.LinkEnrollmentId, linkEnrollmentId), ct);
        return affected == 1;
    }

    /// <summary>
    /// Revoke the token; the caller kills the consumption-linked link
    /// enrollment (by <see cref="RevokeTokenOutcome.ConsumedEndpointId"/>)
    /// so the issued link token dies immediately.
    /// </summary>
    /// <param name="id">Token row id.</param>
    /// <param name="actor">Actor of the revoking principal (audit).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(RevokeTokenOutcome Outcome, string? ConsumedEndpointId)> RevokeAsync(Guid id, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null)
        {
            return (RevokeTokenOutcome.NotFound, null);
        }

        if (row.RevokedAt is not null)
        {
            return (RevokeTokenOutcome.AlreadyRevoked, row.ConsumedEndpointId);
        }

        row.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
        audit.Append(db, new AuditRecord(actor, "admin.enroll-tokens.revoke", $"/api/admin/enroll-tokens/{id}", "allow", null, null, null,
            System.Text.Json.JsonSerializer.Serialize(new { id, label = row.Label, consumedEndpoint = row.ConsumedEndpointId })));
        await db.SaveChangesAsync(ct);
        return (RevokeTokenOutcome.Revoked, row.ConsumedEndpointId);
    }

    private static string NewLiteral()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "et_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
