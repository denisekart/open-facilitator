using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// The single binding-matching law of the gate: "which operation binding, if
/// any, covers this request, and therefore which roles may run it". The
/// ingress gate and the AiDeck catalog filter both ask through here, so "what
/// may this principal invoke" has exactly one answer in the codebase.
/// </summary>
public static class OperationMatcher
{
    /// <summary>
    /// First binding whose method matches and whose route-template patterns
    /// match the resolved endpoint's template (fallback: the request path —
    /// literal segments verbatim, parameters as wildcards).
    /// </summary>
    public static OperationBinding? Match(IReadOnlyList<OperationBinding> operations, HttpContext context, PathString path)
    {
        var endpointTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        foreach (var operation in operations)
        {
            if (!operation.Methods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in operation.Paths)
            {
                if ((endpointTemplate is not null && SegmentsMatch(candidate, endpointTemplate))
                    || SegmentsMatch(candidate, path.Value ?? string.Empty))
                {
                    return operation;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The same law against a literal path, with no endpoint-template branch:
    /// there is no resolved endpoint for a path one merely intends to call,
    /// so callers that ask "may this principal invoke that path" (the AiDeck
    /// catalog slice) match the path itself.
    /// </summary>
    public static OperationBinding? MatchPath(IReadOnlyList<OperationBinding> operations, string method, string path)
    {
        foreach (var operation in operations)
        {
            if (!operation.Methods.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in operation.Paths)
            {
                if (SegmentsMatch(candidate, path))
                {
                    return operation;
                }
            }
        }

        return null;
    }

    // Compare segment by segment. Literal segments must match; a `{param}`
    // on either side matches any segment on the other.
    public static bool SegmentsMatch(string candidate, string other)
    {
        var left = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var right = other.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (IsParameter(left[i]) || IsParameter(right[i]))
            {
                continue;
            }

            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsParameter(string segment) =>
        segment.Length > 1 && segment[0] == '{' && segment[^1] == '}';
}
