using System.Text.Json;

namespace Facilitator.Kernel.Workflows;

/// <summary>One validator finding: machine code + human detail + doc pointer.</summary>
/// <param name="Code">Machine-readable error code (e.g. <c>unknown-command</c>).</param>
/// <param name="Detail">Human detail naming the offending location.</param>
public sealed record WorkflowValidationError(string Code, string Detail);

/// <summary>The validator outcome.</summary>
/// <param name="Errors">Findings; empty = the document is valid.</param>
public sealed record WorkflowValidationResult(IReadOnlyList<WorkflowValidationError> Errors)
{
    /// <summary>Valid = zero findings.</summary>
    public bool Ok => Errors.Count == 0;
}
