namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The constants of the assign-rules port — the seams the importer bakes
/// and the arm's swap knob reads. The template definition
/// (<c>assign-rules</c>) is the gallery document every derived
/// <c>assign-rule-&lt;provider&gt;-&lt;n&gt;</c> copy clones; the arm
/// defers ONLY to derived rows (the bare template is not a scoped rule, so
/// enabling it alone never swallows the arm's Work).
/// </summary>
public static class AssignRulesTemplate
{
    /// <summary>Gallery template id (and the prefix of every derived copy).</summary>
    public const string DefinitionId = "assign-rules";

    /// <summary>The <c>work-create</c> step id of the template shape — the
    /// arm's deterministic-work-id recomputation must agree with the
    /// imported copy, so the id is a constant, not a per-copy detail.</summary>
    public const string CreateStepId = "create";

    /// <summary>The def id of the n-th rule of a provider (n is 1-based
    /// config order): stable across restarts while the config list order
    /// holds; a removed rule leaves its row (the "never removes a seam"
    /// law — the operator owns it from then on).</summary>
    public static string DefinitionIdFor(string provider, int ordinal) =>
        $"assign-rule-{provider}-{ordinal}";
}
