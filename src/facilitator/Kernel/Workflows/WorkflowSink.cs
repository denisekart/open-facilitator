using System.Diagnostics;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The synthetic sink of the workflow plane: channel <c>workflow</c>,
/// recipient <c>workflow.kernel</c> (the <see cref="RoutingIntent"/>
/// precedent, double-idempotency law included). A row is either a TRIGGER
/// (command id <c>wf-trigger:…</c> — a copy of a wire event the plane must
/// start a flow from) or a WAKE (Phase 6: <c>wf-wake:…</c>). Delivery runs
/// the instance to park-or-terminal, one step per transaction; the
/// deterministic instance id + command id make a redelivered row a
/// read-back-and-continue, never a second instance.
/// Boot law (<c>Facilitator:Workflows:Enabled=false</c>): the emitter stops
/// appending; a leftover row (a plane disabled mid-run) requeues fresh
/// (<see cref="RoutePendingException"/> — no DLQ budget, a plane-enabled
/// host or a re-enable still gets it) and is consumed-and-discarded once
/// aged, so a disabled deployment never piles up DLQ noise.
/// </summary>
public sealed class WorkflowSink(
    WorkflowRuntime runtime,
    WorkflowRunner runner,
    WorkflowSubscriptionRegistry subscriptions,
    ILogger<WorkflowSink> logger) : ISink
{
    /// <inheritdoc />
    public string Channel => "workflow";

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
    {
        if (!runtime.Enabled)
        {
            // Leftover law: a plane disabled mid-run must not pile up DLQ
            // noise. A young row requeues under the no-route law (another
            // host with the plane enabled may still claim it, and the row
            // survives for a re-enabled plane); once three claims have all
            // found the plane disabled, no such host is waiting — consume
            // it with a warning.
            if (row.Attempts <= 3)
            {
                logger.LogInformation(
                    "workflow plane disabled — requeueing row {OutboxId} command={CommandId}",
                    row.OutboxId, row.CommandId);
                throw new RoutePendingException("plane-disabled");
            }

            logger.LogWarning(
                "workflow plane disabled — discarding leftover row {OutboxId} command={CommandId}",
                row.OutboxId, row.CommandId);
            return true;
        }

        using var span = KernelActivity.Source.StartActivity("workflow.sink.deliver", ActivityKind.Internal);
        span?.SetTag("messaging.system", "postgres-outbox");
        span?.SetTag("ofac.outbox.command_id", row.CommandId);

        var trigger = WorkflowTrigger.Decode(row);
        if (trigger is not null)
        {
            return await RunAsync(row, trigger, span, ct);
        }

        var wake = WorkflowWake.Decode(row);
        if (wake is not null)
        {
            try
            {
                span?.SetTag("ofac.workflow.instance", wake.InstanceId.ToString());
                await runner.ResumeAsync(wake, ct);
                await subscriptions.RebuildAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "workflow wake failed for {CommandId}; requeueing", row.CommandId);
                return false;
            }
        }

        // Not a plane-shaped row on a plane channel: consumed (a false
        // here would requeue forever, and nothing can fix it).
        logger.LogWarning("workflow sink ignored row {OutboxId} command={CommandId}", row.OutboxId, row.CommandId);
        return true;
    }

    private async Task<bool> RunAsync(OutboxRow row, WorkflowTrigger trigger, Activity? span, CancellationToken ct)
    {
        try
        {
            var instanceId = await runner.StartOrResumeAsync(trigger, ct);

            // Park/unpark law: a new park may have added wait types to the
            // subscription set. Cheap (small tables), post-transaction.
            await subscriptions.RebuildAsync(ct);
            span?.SetTag("ofac.workflow.instance", instanceId.ToString());
            span?.SetTag("ofac.workflow.definition", trigger.DefinitionId + "@" + trigger.DefinitionVersion);
            return true;
        }
        catch (Exception ex)
        {
            // A plane error requeues (the ISink law); the per-instance
            // advisory lock + read-back make the retry cheap and safe.
            logger.LogWarning(ex, "workflow delivery failed for {CommandId}; requeueing", row.CommandId);
            return false;
        }
    }
}

/// <summary>Boot options of the plane (<c>Facilitator:Workflows</c>).</summary>
public sealed class WorkflowOptions
{
    /// <summary>Config section.</summary>
    public const string SectionName = "Facilitator:Workflows";

    /// <summary>Master switch: when false the trigger emitter never appends
    /// and the sink drains leftovers. False by default everywhere (the
    /// gallery seeds definitions, operators enable both).</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the sweeper runs (safety-net cadence, not the
    /// clock of any individual deadline).</summary>
    public int SweepIntervalSeconds { get; set; } = 15;
}

/// <summary>Runtime state shared by the emitter, sink and sweeper: the boot
/// switch (one instance per host). Tests flip <see cref="Enabled"/> on the
/// resolved singleton — the composition root never does.</summary>
public sealed class WorkflowRuntime(WorkflowOptions options)
{
    /// <summary>True when the plane executes (boot flag).</summary>
    public bool Enabled { get; set; } = options.Enabled;
}
