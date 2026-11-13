using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The approval wake leg: a decided approval (via REST respond, consume,
/// revoke, or lazy/explicit expiry) resumes the parked flow that raised
/// it. Rides the <see cref="IApprovalDecisionObserver"/> seam inside the
/// decision transaction, so the decision and the wake row commit together
/// (outbox law) even when the approval never touched a Link endpoint. The
/// correlation is the approval id itself — the parked approval step stored
/// it as its correlation key.
/// </summary>
public sealed class WorkflowApprovalWake(WorkflowRuntime runtime, TimeProvider timeProvider) : IApprovalDecisionObserver
{
    /// <inheritdoc />
    public Task OnApprovalDecidedAsync(FacilitatorDbContext db, JsonElement envelope, CancellationToken ct)
    {
        if (!runtime.Enabled
            || !envelope.TryGetProperty("data", out var data)
            || !data.TryGetProperty("approvalId", out var approvalIdElement)
            || approvalIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(approvalIdElement.GetString(), out var approvalId))
        {
            return Task.CompletedTask;
        }

        var correlationKey = approvalId.ToString("N");
        var parked = db.WorkflowInstances.AsNoTracking()
            .Where(i => i.Status == "waiting")
            .ToList()
            .FirstOrDefault(i => WorkflowTriggerEmitter.ParkedCorrelationKey(db, i) == correlationKey);
        if (parked is null)
        {
            return Task.CompletedTask;
        }

        WorkflowTriggerEmitter.AppendWake(
            db,
            parked.InstanceId,
            parked.PrimaryWorkId,
            approvalId, // the decision's identity doubles as the wake-row seed
            envelope,
            timeProvider.GetUtcNow().UtcDateTime);
        return Task.CompletedTask;
    }
}
