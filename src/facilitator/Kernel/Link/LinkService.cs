using System.Data.Common;

using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Link;

/// <summary>A live (lease-unexpired) session view.</summary>
/// <param name="EndpointId">Logical endpoint.</param>
/// <param name="SessionEpoch">Current owner epoch (spec §7.3).</param>
/// <param name="CapabilitiesDigest">Digest advertised at Hello, when any.</param>
public sealed record LiveSession(string EndpointId, long SessionEpoch, string? CapabilitiesDigest);

/// <summary>A session whose lease has passed (sweeper input).</summary>
/// <param name="EndpointId">Logical endpoint.</param>
/// <param name="SessionEpoch">Epoch of the expired session.</param>
public sealed record ExpiredSession(string EndpointId, long SessionEpoch);

/// <summary>
/// DB-side session operations over <c>link_sessions</c> (spec §7.3): the
/// lease — not socket state — is the authority on liveness. Every clock read
/// flows through <see cref="TimeProvider"/> so lease math is testable without
/// wall-clock sleeps. Raw SQL mirrors <c>IngressStore</c> (no change tracker
/// on hot paths).
/// </summary>
public sealed class LinkService(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private readonly string _replicaId = configuration["Kernel:ReplicaId"]
        ?? $"{Environment.MachineName}/{Guid.NewGuid():N}";

    /// <summary>Replica identity stamped on every session row (spec §7.3 placement).</summary>
    public string ReplicaId => _replicaId;

    /// <summary>
    /// Open or supersede: epoch = max(existing)+1, lease = now +
    /// heartbeatSeconds*3 (clamped ≥ 15 s). Returns the new epoch; a second
    /// Hello while a live session exists bumps past it (caller pushes
    /// <c>Disconnect</c> to the old connection — spec §16.2).
    /// </summary>
    public async Task<long> OpenSessionAsync(string endpointId, string instanceId, string? capabilitiesDigest, int heartbeatSeconds, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lease = now.Add(TimeSpan.FromSeconds(Math.Max(15, heartbeatSeconds * 3L)));
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO facilitator.link_sessions
                (logical_endpoint_id, connection_instance_id, replica_id, session_epoch, lease_until, last_heartbeat, capabilities_digest)
            VALUES
                (@endpoint, @instance, @replica, 1, @lease, @now, @digest)
            ON CONFLICT (logical_endpoint_id) DO UPDATE
               SET connection_instance_id = @instance,
                   replica_id = @replica,
                   session_epoch = facilitator.link_sessions.session_epoch + 1,
                   lease_until = @lease,
                   last_heartbeat = @now,
                   capabilities_digest = @digest
            RETURNING session_epoch
            """;
        Add(command, "@endpoint", endpointId);
        Add(command, "@instance", instanceId);
        Add(command, "@replica", _replicaId);
        Add(command, "@lease", lease);
        Add(command, "@now", now);
        Add(command, "@digest", (object?)capabilitiesDigest ?? DBNull.Value);
        var epoch = await ScalarAsync(command, ct);
        return epoch is long e ? e : throw new InvalidOperationException("session upsert returned no epoch");
    }

    /// <summary>Refresh lease + last_heartbeat ONLY for a matching (endpoint, epoch); false = stale.</summary>
    public async Task<bool> TouchSessionAsync(string endpointId, long sessionEpoch, int heartbeatSeconds, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lease = now.Add(TimeSpan.FromSeconds(Math.Max(15, heartbeatSeconds * 3L)));
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.link_sessions
                SET lease_until = {lease}, last_heartbeat = {now}
              WHERE logical_endpoint_id = {endpointId} AND session_epoch = {sessionEpoch}
             """, ct);
        return rows == 1;
    }

    /// <summary>The live session for an endpoint (lease unexpired); null when absent/expired.</summary>
    public async Task<LiveSession?> TryGetLiveAsync(string endpointId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.LinkSessions.AsNoTracking()
            .Where(s => s.LogicalEndpointId == endpointId && s.LeaseUntil > now)
            .Select(s => new LiveSession(s.LogicalEndpointId, s.SessionEpoch, s.CapabilitiesDigest))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Revoke the session row when the epoch matches (goodbye acks nothing).</summary>
    public async Task CloseSessionAsync(string endpointId, long sessionEpoch, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM facilitator.link_sessions
              WHERE logical_endpoint_id = {endpointId} AND session_epoch = {sessionEpoch}
             """, ct);
    }

    /// <summary>Tracked sessions whose lease has passed; their rows are deleted (sweeper closes their sockets).</summary>
    public async Task<IReadOnlyList<ExpiredSession>> SweepExpiredAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.LinkSessions.AsNoTracking()
            .Where(s => s.LeaseUntil <= now)
            .Select(s => new ExpiredSession(s.LogicalEndpointId, s.SessionEpoch))
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            await db.LinkSessions
                .Where(s => s.LeaseUntil <= now)
                .ExecuteDeleteAsync(ct);
        }

        return rows;
    }

    /// <summary>Live-session count (facilitator.link.sessions gauge).</summary>
    public async Task<long> CountLiveAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.LinkSessions.AsNoTracking().CountAsync(s => s.LeaseUntil > now, ct);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<object?> ScalarAsync(DbCommand command, CancellationToken ct)
    {
        var connection = command.Connection ?? throw new InvalidOperationException("command has no connection");
        await connection.OpenAsync(ct);
        try
        {
            return await command.ExecuteScalarAsync(ct);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
