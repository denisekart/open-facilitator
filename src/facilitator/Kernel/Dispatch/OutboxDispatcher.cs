using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Facilitator.Kernel.Dispatch;

/// <summary>
/// Thin timer over <see cref="IEventDispatcher"/> (no new container, spec
/// §25.3): 250 ms when busy, exponential backoff to 5 s when idle, plus a
/// <c>LISTEN facilitator_outbox</c> wake on a dedicated connection. NOTIFY is
/// a hint only — correctness never depends on the wake — so every failure on
/// the listener or on a drain is caught and logged; the loop degrades to
/// polling and never crashes the host (health tests run without a database).
/// </summary>
public sealed class OutboxDispatcher(
    IEventDispatcher dispatcher,
    IDbContextFactory<Persistence.FacilitatorDbContext> contextFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        var listenerTask = ListenAsync(wake, stoppingToken);

        var delay = MinDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivered = await dispatcher.DrainOnceAsync(stoppingToken);
                delay = delivered == 0 ? Cap(delay * 2) : MinDelay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "outbox drain failed; continuing to poll");
            }

            try
            {
                await dispatcher.ReclaimExpiredLeasesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "lease reclaim failed; continuing to poll");
            }

            try
            {
                await wake.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await listenerTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "outbox LISTEN loop ended");
        }
    }

    private static TimeSpan Cap(TimeSpan candidate) => candidate > MaxDelay ? MaxDelay : candidate;

    private async Task ListenAsync(SemaphoreSlim wake, CancellationToken ct)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            connection.Notification += (_, e) =>
            {
                if (e.Channel == "facilitator_outbox")
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
            };

            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "LISTEN facilitator_outbox";
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
            logger.LogWarning(ex, "outbox LISTEN wake lost; dispatcher continues polling");
        }
    }
}
