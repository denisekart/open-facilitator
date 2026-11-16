using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Persistence;


namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>
/// Session-JWT <see cref="ILinkTokenValidator"/>: parse the bearer, verify its
/// EdDSA signature against the kernel's shared session key, check
/// <c>exp</c>, then compare the <c>epoch</c> claim against a cached enrollment
/// row. The cache follows the <c>LinkConnectionRegistry</c>/<c>PgWakeListener</c>
/// pattern — the revocation NOTIFY is a hint that invalidates it early, the TTL
/// is the correctness floor — so a revoke lands within a TTL window even if the
/// wake is lost, and inside milliseconds when it is not. No secret lookup: the
/// token is self-describing, and identity is the key that signed the proof.
/// </summary>
public sealed class StoreLinkTokenValidator(
    LinkEnrollmentStore store,
    LinkSessionKeys sessionKeys,
    TimeProvider timeProvider) : ILinkTokenValidator, ILinkRevocationSink
{
    /// <summary>How long a per-endpoint auth row may serve before it is re-read.
    /// A lost revocation NOTIFY costs at most this much staleness.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed record Entry(long Epoch, bool Dead, LinkPrincipal? Principal);

    private readonly Dictionary<string, (DateTimeOffset At, Entry Entry)> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc />
    public void Invalidate(string endpointId)
    {
        lock (gate)
        {
            cache.Remove(endpointId);
        }
    }

    /// <inheritdoc />
    public async Task<LinkPrincipal?> ValidateAsync(string token, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var sessionKey = await sessionKeys.PublicKeyAsync(ct);
        if (sessionKey is null
            || !LinkSessionJwt.TryVerify(token, sessionKey.Value.PublicKey, now.UtcDateTime, out var claims)
            || claims is null)
        {
            return null;
        }

        Entry? cached = null;
        lock (gate)
        {
            if (cache.TryGetValue(claims.Subject, out var hit) && now - hit.At < CacheTtl)
            {
                cached = hit.Entry;
            }
        }

        if (cached is null)
        {
            var subject = await store.GetSessionSubjectAsync(claims.Subject, ct);
            cached = subject is null
                ? new Entry(-1, true, null)
                : new Entry(
                    subject.Epoch,
                    subject.Dead,
                    new LinkPrincipal(
                        claims.Subject,
                        subject.TeamId,
                        subject.TeamSlug,
                        subject.TrustMode,
                        subject.Role,
                        subject.Epoch,
                        claims.Seq,
                        claims.AuthSource,
                        sessionKey.Value.Kid));
            lock (gate)
            {
                cache[claims.Subject] = (now, cached);
            }
        }

        // The epoch law: a credential minted at a stale epoch is dead the moment
        // the revoke bumps the row — no session table involved.
        if (cached.Dead || cached.Epoch != claims.Epoch)
        {
            return null;
        }

        // The cached principal carried the mint-time seq; rebuild it with the
        // claim's own seq so a caller can fence without a second query.
        return cached.Principal is null ? null : cached.Principal with { Seq = claims.Seq };
    }
}
