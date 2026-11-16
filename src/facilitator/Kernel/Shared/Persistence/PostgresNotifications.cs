using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>
/// The ONE Postgres wake-notify form: <c>pg_notify(channel, payload)</c> with
/// a parameterized payload. NOTIFY is a hint only — correctness
/// never depends on the wake, only latency does.
/// </summary>
public static class PostgresNotifications
{
    /// <summary>The outbox wake channel every dispatcher/pump LISTENs on.</summary>
    public const string OutboxChannel = "facilitator_outbox";

    /// <summary>The link-revocation channel: a revoke bumps an enrollment's
    /// epoch, replicas LISTEN here to invalidate their per-endpoint auth cache
    /// early. A hint like every other notify — the cache TTL is the
    /// correctness floor, the wake only buys freshness.</summary>
    public const string LinkRevocationChannel = "facilitator_link_revocation";

    /// <summary>Send the link-revocation wake (payload: the endpointId).</summary>
    public static Task NotifyRevocationAsync(DbContext db, string endpointId, CancellationToken ct)
    {
        object[] parameters = [endpointId];
        return db.Database.ExecuteSqlRawAsync($"SELECT pg_notify('{LinkRevocationChannel}', {{0}})", parameters, ct);
    }

    /// <summary>Send the outbox wake (optional payload, e.g. the aggregate id).</summary>
    public static Task NotifyOutboxAsync(DbContext db, string? payload, CancellationToken ct)
    {
        object[] parameters = [payload ?? string.Empty];
        return db.Database.ExecuteSqlRawAsync($"SELECT pg_notify('{OutboxChannel}', {{0}})", parameters, ct);
    }

    /// <summary>Send the outbox wake on a fresh context from the factory.</summary>
    public static async Task NotifyOutboxAsync(IDbContextFactory<FacilitatorDbContext> contextFactory, string? payload, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await NotifyOutboxAsync(db, payload, ct);
    }
}
