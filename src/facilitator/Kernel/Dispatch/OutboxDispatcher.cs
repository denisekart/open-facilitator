using Facilitator.Kernel.Shared.Persistence;

namespace Facilitator.Kernel.Dispatch;

/// <summary>
/// Thin timer over <see cref="IEventDispatcher"/> (no new container, spec
/// §25.3): 250 ms when busy, exponential backoff to 5 s when idle, plus a
/// <c>LISTEN facilitator_outbox</c> wake (<see cref="PgWakeListener"/>).
/// NOTIFY is a hint only — correctness never depends on the wake — so every
/// failure on the listener or on a drain is caught and logged; the loop
/// degrades to polling and never crashes the host (health tests run without a
/// database).
/// </summary>
public sealed class OutboxDispatcher(
    IEventDispatcher dispatcher,
    PgWakeListener wakeListener,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        var listenerTask = wakeListener.ListenAsync(wake, stoppingToken);

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
}
