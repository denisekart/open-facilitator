namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The composition budget law (preemption guard 2/4 of
/// <see cref="WorkflowPreemption"/>): every denial end-reason is produced by
/// exactly one named guard, and the budgets are constants — a flow can only
/// widen them per definition within the caps.
/// </summary>
public static class WorkflowLimits
{
    /// <summary>Maximum composition depth (guard 2: denial =
    /// <c>chain-age-exceeded</c>).</summary>
    public const int MaxDepth = 5;

    /// <summary>Maximum total invoke entries in one ancestor chain
    /// (guard 2: denial = <c>chain-age-exceeded</c>).</summary>
    public const int MaxChainInvocations = 64;

    /// <summary>Maximum total age of one emit/wake chain, carried from the
    /// root as the instance deadline cap (guard 4 face of lineage).</summary>
    public static readonly TimeSpan MaxChainAge = TimeSpan.FromHours(24);

    /// <summary>Default per-definition invocation cap along a chain when the
    /// definition declares no <c>composition.maxInvocations</c>.</summary>
    public const int DefaultMaxInvocations = 20;
}
