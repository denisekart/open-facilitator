using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>A replica-local invalidation hook for link revocations (implemented by
/// the session validator's epoch cache). NOTIFY is a hint: a miss costs a TTL
/// window of staleness, never correctness.</summary>
public interface ILinkRevocationSink
{
    /// <summary>Drop any cached auth state for this endpoint.</summary>
    void Invalidate(string endpointId);
}

/// <summary>
/// The ONE <c>LISTEN facilitator_outbox</c> wake loop: a dedicated connection
/// releases the caller's semaphore on every wake notification. NOTIFY is a
/// hint only — a lost listener costs latency, never correctness,
/// so every failure is caught and logged and the caller degrades to polling.
/// </summary>
public sealed class PgWakeListener(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    ILinkRevocationSink? revocationSink,
    ILogger<PgWakeListener> logger)
{
    /// <summary>
    /// Run the listener until cancellation: release <paramref name="wake"/>
    /// (dropping redundant releases while one is pending) for every
    /// <c>facilitator_outbox</c> notification, and hand every
    /// <c>facilitator_link_revocation</c> payload to the
    /// <see cref="ILinkRevocationSink"/> (freshness hint for the link auth
    /// cache — correctness stays with its TTL).
    /// </summary>
    public async Task ListenAsync(
        SemaphoreSlim wake,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            connection.Notification += (_, e) =>
            {
                if (e.Channel == PostgresNotifications.OutboxChannel)
                {
                    try
                    {
                        wake.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        // a wake is already pending
                    }
                }
                else if (e.Channel == PostgresNotifications.LinkRevocationChannel && e.Payload.Length > 0)
                {
                    // Freshness only: a lost wake costs a cache-TTL window, never correctness.
                    revocationSink?.Invalidate(e.Payload);
                }
            };

            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"LISTEN {PostgresNotifications.OutboxChannel}; LISTEN {PostgresNotifications.LinkRevocationChannel}";
                await command.ExecuteNonQueryAsync(ct);
            }

            while (!ct.IsCancellationRequested)
            {
                await connection.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            // NOTIFY is a hint: losing the listener only costs latency.
            logger.LogWarning(ex, "outbox LISTEN wake lost; consumer continues polling");
        }
    }
}
