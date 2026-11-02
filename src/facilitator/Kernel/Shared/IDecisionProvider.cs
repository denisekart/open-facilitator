namespace Facilitator.Kernel.Shared;

/// <summary>Decision kinds the seam is consulted for. Only the
/// deterministic authorization evaluator may allow/deny; providers return
/// PREFERENCES over a kernel-computed allowed set, and out-of-set answers
/// fall back deterministically, audited.</summary>
public enum DecisionKind
{
    /// <summary>Which prior context/memory a step should see.</summary>
    ContextResolution,

    /// <summary>Which allowed candidate takes a routing intent.</summary>
    Routing,

    /// <summary>How/whether to speak to a human.</summary>
    Communication,

    /// <summary>Whether an action needs a human gate.</summary>
    Authorization,
}

/// <summary>A candidate the kernel would allow for this decision, pre-ranked
/// by the kernel's deterministic order (index 0 = the deterministic pick).</summary>
/// <param name="EndpointId">The candidate endpoint id.</param>
/// <param name="Specificity">Capability-descriptor count (ranking input the provider may reason over).</param>
/// <param name="OpenedAt">When the candidate's live session opened (oldest-online tiebreak).</param>
public sealed record RoutingCandidate(string EndpointId, int Specificity, DateTime OpenedAt);

/// <summary>A provider's advisory answer: a preferred candidate plus rationale.
/// The kernel adopts the preference only when the candidate is in the allowed
/// set; anything else keeps the deterministic order.</summary>
public sealed record DecisionOutcome(string PreferredCandidate, string Rationale, double? Confidence);

/// <summary>The advisory decision seam. Implementations must be
/// side-effect-free and cancellation-respecting; a null return means "no
/// preference — the deterministic order stands". The shipped
/// <see cref="DeterministicDecisionProvider"/> always returns null; a model
/// provider (reserved) consults facts over the allowed set only.</summary>
public interface IDecisionProvider
{
    /// <summary>Stable identity recorded in audit rows (provider_ref).</summary>
    string Name { get; }

    /// <summary>Evaluate a preference over <paramref name="allowedCandidates"/>
    /// (already in deterministic order). Null = no preference.</summary>
    Task<DecisionOutcome?> EvaluateAsync(
        DecisionKind kind,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyList<RoutingCandidate> allowedCandidates,
        CancellationToken ct);
}

/// <summary>The only shipped provider: never expresses a preference, so the
/// kernel's deterministic order always holds.</summary>
public sealed class DeterministicDecisionProvider : IDecisionProvider
{
    /// <inheritdoc />
    public string Name => "deterministic";

    /// <inheritdoc />
    public Task<DecisionOutcome?> EvaluateAsync(
        DecisionKind kind,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyList<RoutingCandidate> allowedCandidates,
        CancellationToken ct)
    {
        _ = kind;
        _ = facts;
        _ = allowedCandidates;
        _ = ct;
        return Task.FromResult<DecisionOutcome?>(null);
    }
}
