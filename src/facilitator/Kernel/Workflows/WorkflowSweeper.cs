using System.Diagnostics;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The workflow sweeper: the plane's safety net, not the clock of any
/// single deadline (the same posture as <c>ApprovalSweeper</c>). Every
/// waiting instance past <c>deadline_at</c> closes its park as timed out
/// and continues — a <c>timer</c> completes, a <c>wait</c>/<c>approval</c>
/// takes its timeout/failure edge. One pass takes an advisory lock so
/// replicas sweep once; the per-instance conditional UPDATE resolves any
/// wake-vs-timeout race regardless (first flip wins). The admin
/// <c>POST /api/admin/sweeps</c> switch can run a tick on demand.
/// </summary>
public sealed class WorkflowSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    WorkflowRuntime runtime,
    WorkflowRunner runner,
    ILogger<WorkflowSweeper> logger,
    TimeProvider timeProvider,
    WorkflowOptions options) : BackgroundService
{
    /// <summary>Advisory-lock key of the sweep pass (replica-singleton).
    /// The registry: 420001 migration, 420002 storage GC, 420003 memory GC,
    /// 420004 provisioning, 420005 nudge, 420006 schedule+approvals,
    /// 420007 workflows.</summary>
    public const int SweepLockKey = 420007;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.SweepIntervalSeconds)), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                // The sweep is advisory: one bad pass must never kill the loop.
                logger.LogWarning(ex, "workflow sweep pass failed; retrying next tick");
            }
        }
    }

    /// <summary>One tick: every due park fires its timeout leg. Returns the
    /// count fired (also the admin-trigger response).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (!runtime.Enabled)
        {
            return 0;
        }

        using var activity = KernelActivity.Source.StartActivity("workflow.sweep", ActivityKind.Internal);
        var fired = 0;
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (await TrySweepLockAsync(db, ct)
                && await db.WorkflowInstances.AsNoTracking().AnyAsync(
                    i => i.Status == "waiting" && i.DeadlineAt != null && i.DeadlineAt <= now, ct))
            {
                // Due parks exist: each instance leg takes its own advisory
                // lock and conditional flip outside this sweep lock.
                fired = await runner.SweepDeadlinesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        activity?.SetTag("ofac.workflow.deadlines_fired", fired);
        return fired;
    }

    private static async Task<bool> TrySweepLockAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT pg_try_advisory_xact_lock({SweepLockKey})";
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }
}
