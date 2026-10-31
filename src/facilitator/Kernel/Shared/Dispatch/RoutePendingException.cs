namespace Facilitator.Kernel.Shared.Dispatch;

/// <summary>
/// Sink signal: the row is NOT deliverable yet, but must never dead-letter —
/// requeue with backoff and keep the claim-leased attempt uncharged against
/// the DLQ budget. The routing funnel raises it while an intent has no
/// allowed candidate (the endpoint may still boot; a pending
/// <c>routing_decisions</c> row carries the reason for the scale-up trigger).
/// </summary>
public sealed class RoutePendingException(string reason) : Exception(reason)
{
    /// <summary>The requeue reason recorded on the sink's own audit row.</summary>
    public string Reason { get; } = reason;
}
