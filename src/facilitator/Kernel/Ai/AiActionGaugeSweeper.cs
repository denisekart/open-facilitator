using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// Refreshes <c>facilitator.ai.chat.actions.pending</c> on the same cadence
/// idea as the dispatcher's queue gauges: cheap COUNT over
/// <c>facilitator.chat_actions</c>. Actions execute only on human confirm, so
/// the gauge (not a pump) is the right surface — a rising value is the
/// operator's "proposals awaiting review" signal.
/// </summary>
public sealed class AiActionGaugeSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    ILogger<AiActionGaugeSweeper> logger) : BackgroundService
{
    /// <summary>Seconds between gauge passes.</summary>
    public const int IntervalSeconds = 30;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ai action gauge pass failed");
            }
        }
    }

    /// <summary>One pass (public for tests to drive it without waiting).
    /// Expire first, then count: a silently-expired proposal becomes a
    /// visible terminal state, so the gauge and the cards always agree.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.ChatActions
            .Where(a => a.Status == "pending" && a.ExpiresAt != null && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "expired"), ct);
        metrics.SetAiPendingActions(await db.ChatActions.CountAsync(a => a.Status == "pending", ct));
    }
}
