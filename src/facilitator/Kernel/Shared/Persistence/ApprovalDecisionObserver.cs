using System.Text.Json;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>
/// Kernel-internal observer of a terminal approval decision (approved,
/// denied, expired, revoked). The <see cref="ApprovalLedger"/> notifies
/// every registered observer inside the decision transaction — before the
/// link-push endpoint check — so a decision made on the REST plane can
/// resume kernel-plane parks (the workflow plane's approval wake) even
/// when no Link endpoint ever learns about the approval. Implementations
/// must be cheap and must not throw on plane-absence conditions; a failed
/// append rides the decision transaction's rollback, never the reverse.
/// </summary>
public interface IApprovalDecisionObserver
{
    /// <summary>Called once per terminal transition, on the decision's own
    /// transaction/context. <paramref name="envelope"/> is the conformant
    /// <c>approval.decided</c> envelope the push would carry.</summary>
    Task OnApprovalDecidedAsync(
        FacilitatorDbContext db, JsonElement envelope, CancellationToken ct);
}
