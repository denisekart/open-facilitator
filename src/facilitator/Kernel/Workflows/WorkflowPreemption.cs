namespace Facilitator.Kernel.Workflows;

/// <summary>A preemption denial: which guard fired and what it names.</summary>
/// <param name="EndReason">The recorded end reason
/// (<c>budget-exhausted</c> | <c>chain-age-exceeded</c>).</param>
/// <param name="Detail">Human detail (naming the path or count).</param>
public sealed record PreemptionDenial(string EndReason, string Detail);

/// <summary>The outcome of admitting a child candidate.</summary>
public enum PreemptionOutcome
{
    Admitted,
    Denied,
}

/// <summary>One evaluated admit decision.</summary>
public sealed record PreemptionDecision(PreemptionOutcome Outcome, PreemptionDenial? Denial)
{
    /// <summary>Admitted = every guard passed.</summary>
    public bool Admitted => Outcome == PreemptionOutcome.Admitted;
}

/// <summary>
/// The preemption law, in ONE place (guard 1 of
/// <see cref="WorkflowPreemption.Run"/> is also the validator's enable-time
/// static check — one law, one implementation):
/// <list type="number">
/// <item><b>Static cycles</b> over the enabled invoke-call graph
/// (<c>composition-cycle</c> at enable time, 422).</item>
/// <item><b>Chain budget</b>: depth, chain length, per-definition occurrence
/// cap (<c>budget-exhausted</c> for the per-definition count,
/// <c>chain-age-exceeded</c> for depth/path overflow — every end_reason is
/// produced by exactly one named guard).</item>
/// </list>
/// Guards 3 (denial-is-a-branch) and 4 (age + death) are the runner's step
/// semantics, not this function; they consume the decision, never re-decide.
/// </summary>
public static class WorkflowPreemption
{
    /// <summary>Decide whether a child candidate may be admitted.
    /// <paramref name="candidatePath"/> is the child's would-be invocation
    /// path (parent path + <c>&gt;targetId@ver</c>), <paramref name="depth"/>
    /// the child's would-be depth, <paramref name="candidateIdVer"/> the
    /// target's <c>defId@ver</c>, <paramref name="reentrant"/> the target
    /// definition's reentrancy, <paramref name="maxInvocations"/> the target's
    /// per-chain cap.</summary>
    public static PreemptionDecision TryAdmit(
        string candidatePath,
        int depth,
        string candidateIdVer,
        bool reentrant,
        int maxInvocations)
    {
        if (depth > WorkflowLimits.MaxDepth)
        {
            return Denied("chain-age-exceeded", $"depth {depth} exceeds the cap {WorkflowLimits.MaxDepth}");
        }

        var entries = candidatePath.Split('>');
        if (entries.Length > WorkflowLimits.MaxChainInvocations)
        {
            return Denied("chain-age-exceeded",
                $"invocation path has {entries.Length} entries, the chain cap is {WorkflowLimits.MaxChainInvocations}");
        }

        var occurrences = entries.Count(e => string.Equals(e, candidateIdVer, StringComparison.Ordinal));
        if (occurrences > 1 && !reentrant)
        {
            return Denied("budget-exhausted",
                $"'{candidateIdVer}' appears {occurrences} times in one chain but its definition is not reentrant");
        }

        if (occurrences > maxInvocations)
        {
            return Denied("budget-exhausted",
                $"'{candidateIdVer}' would run {occurrences} times in one chain (cap {maxInvocations})");
        }

        return new(PreemptionOutcome.Admitted, null);
    }

    /// <summary>The static-cycle law over an invoke-call graph (edges of
    /// <c>from defId@ver</c> -> <c>to defId@ver</c>). Returns the first cycle
    /// found (path form a -&gt; b -&gt; a) or null. Definition targets with a
    /// live-version resolution are pre-resolved by the caller: the guard stays
    /// a pure graph walk.</summary>
    public static IReadOnlyList<string>? FindStaticCycle(IEnumerable<(string From, string To)> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            nodes.Add(from);
            nodes.Add(to);
            (adjacency.TryGetValue(from, out var list) ? list : adjacency[from] = []).Add(to);
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        IReadOnlyList<string>? Walk(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in adjacency.TryGetValue(node, out var list) ? list : [])
            {
                if (state.TryGetValue(next, out var s) && s == 1)
                {
                    var at = stack.IndexOf(next);
                    return [.. stack.GetRange(at, stack.Count - at), next];
                }

                if (!state.ContainsKey(next) && Walk(next) is { } deeper)
                {
                    return deeper;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            return null;
        }

        foreach (var node in nodes)
        {
            if (!state.ContainsKey(node) && Walk(node) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }

    private static PreemptionDecision Denied(string endReason, string detail) =>
        new(PreemptionOutcome.Denied, new PreemptionDenial(endReason, detail));
}
