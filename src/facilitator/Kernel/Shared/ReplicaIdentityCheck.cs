using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared;

/// <summary>
/// Fail-closed startup gate for horizontal replicas (Plan 35 D): a configured
/// <c>Kernel:ReplicaId</c> is the lease identity stamped on link sessions and
/// outbox claims, so two live processes sharing one id interleave leases
/// under an indistinguishable owner. At startup, every session row already
/// stamped with MY id and a live lease is either my dead predecessor's
/// leftover or a live twin: a probe window of one heartbeat interval decides
/// honestly — only a live process can ADVANCE a lease (heartbeats extend it),
/// so an advancing row means a twin is running (refuse to start), and a row
/// that goes unadvanced is leftover state (delete it; the gateway's next
/// Hello re-stamps it at the next epoch).
/// </summary>
public sealed class ReplicaIdentityCheck(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<ReplicaIdentityCheck> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Unset ids are process-unique by construction (machine + GUID): the
        // gate only guards explicitly configured identities.
        if (configuration["Kernel:ReplicaId"] is not { Length: > 0 } replicaId)
        {
            return;
        }

        var heartbeat = configuration.GetValue<int?>("Link:HeartbeatSeconds") ?? 30;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mine = await MineAsync(db, replicaId, cancellationToken);
        if (mine.Count == 0)
        {
            return;
        }

        // One heartbeat interval + a beat: a twin's gateway heartbeats every
        // interval, so a live owner cannot sit out the whole window.
        await Task.Delay(TimeSpan.FromSeconds(heartbeat + 2), cancellationToken);
        var after = await MineAsync(db, replicaId, cancellationToken);
        foreach (var (endpoint, leaseUntil) in mine)
        {
            if (after.TryGetValue(endpoint, out var lease) && lease > leaseUntil)
            {
                throw new InvalidOperationException(
                    $"Kernel:ReplicaId '{replicaId}' collides with a live peer: session " +
                    $"'{endpoint}' stamped with this id has an advancing lease. " +
                    "Refusing to start (fail closed).");
            }

            // Unadvanced (or expired between reads): leftover from a dead
            // incarnation of THIS id — revoke it; a live gateway would have
            // advanced the lease.
            await db.LinkSessions
                .Where(s => s.LogicalEndpointId == endpoint && s.ReplicaId == replicaId)
                .ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation(
                "revoked stale link session {Endpoint} stamped with replica id {ReplicaId}",
                endpoint, replicaId);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<Dictionary<string, DateTime>> MineAsync(
        FacilitatorDbContext db, string replicaId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return (await db.LinkSessions.AsNoTracking()
                .Where(s => s.ReplicaId == replicaId && s.LeaseUntil > now)
                .Select(s => new { s.LogicalEndpointId, s.LeaseUntil })
                .ToListAsync(ct))
            .ToDictionary(r => r.LogicalEndpointId, r => r.LeaseUntil, StringComparer.Ordinal);
    }
}
