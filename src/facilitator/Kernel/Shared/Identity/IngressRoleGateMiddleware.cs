using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// The kernel-edge human-plane gate, placed after <c>UseAuthorization</c> so
/// the resolved endpoint (route template + method) is available:
/// <list type="bullet">
/// <item>Outside <c>/api/*</c> and on the link surface (<c>/api/link/v1*</c>):
/// untouched — the link plane guards itself.</item>
/// <item>Inside <c>/api/*</c>: an authenticated OIDC principal is REQUIRED —
/// anonymous answers 401 with the standing ProblemDetails.</item>
/// <item>The payload is mapped through the operator's claim pointers, roles
/// are evaluated against the <see cref="RoleStore"/> snapshot, and the
/// endpoint's route template + method are matched against
/// <c>identity:authorization:operations</c>. No matching binding ⇒ any
/// authenticated principal passes. Matching binding ⇒ ≥1 of
/// <c>rolesAnyOf</c> or 403 <c>forbidden</c>.</item>
/// <item>Every binding evaluation writes an audit row (allow and deny). An
/// audit failure on an allow path fails closed with 503 auth-unavailable.</item>
/// </list>
/// </summary>
public sealed class IngressRoleGateMiddleware(
    RequestDelegate next,
    IOptions<IdentityOptions> options,
    RoleStore roles,
    AuditWriter audit,
    KernelMetrics metrics)
{
    /// <summary>Terminal handler for the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/link/v1", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var result = await context.AuthenticateAsync(IdentityAuthentication.Scheme);
        if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
        {
            await IdentityAuthentication.WriteProblemAsync(
                context, IdentityAuthentication.Unauthorized(), context.RequestAborted);
            return;
        }

        // The verified payload rode here from IdentityJwt.OnTokenValidated;
        // absence means the token never passed validation — unauthenticated.
        if (context.Items[IdentityAuthentication.PayloadItem] is not JsonElement payload)
        {
            await IdentityAuthentication.WriteProblemAsync(
                context, IdentityAuthentication.Unauthorized(), context.RequestAborted);
            return;
        }

        var iss = result.Principal.FindFirst("iss")?.Value ?? options.Value.Oidc.Authority;
        var ruleSets = await roles.CurrentRulesAsync(context.RequestAborted);
        var evaluation = RoleEvaluator.Evaluate(payload, ruleSets);
        var principal = PrincipalMapper.Map(payload, iss, options.Value).WithRoles(evaluation.Granted);
        context.Items[IdentityAuthentication.PrincipalItem] = principal;

        var binding = MatchBinding(options.Value.Authorization.Operations, context, path);
        if (binding is null)
        {
            // No binding on this route: any authenticated principal passes
            // (read/dev surface). Only binding evaluations are audited.
            await next(context);
            return;
        }

        if (binding.RolesAnyOf.Any(principal.Roles.Contains))
        {
            try
            {
                await audit.WriteAsync(Decision(principal, binding, "allow", "role-match", ruleSets.Count, roles.PolicyVersion), context.RequestAborted);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                // Fail closed: the decision could not be recorded.
                await audit.WriteAsync(Decision(principal, binding, "deny", "audit-unavailable", ruleSets.Count, roles.PolicyVersion), CancellationToken.None);
                metrics.IdentityDecision("deny");
                await IdentityAuthentication.WriteProblemAsync(
                    context, IdentityAuthentication.AuthUnavailable(), CancellationToken.None);
                return;
            }

            metrics.IdentityDecision("allow");
            await next(context);
            return;
        }

        await audit.WriteAsync(Decision(principal, binding, "deny", "no-role-match", ruleSets.Count, roles.PolicyVersion), context.RequestAborted);
        metrics.IdentityDecision("deny");
        await IdentityAuthentication.WriteProblemAsync(
            context, IdentityAuthentication.Forbidden(binding.Id, binding.RolesAnyOf), context.RequestAborted);
    }

    private static AuditRecord Decision(FacilitatorPrincipal principal, OperationBinding binding, string decision, string reason, int ruleCount, string policyVersion) =>
        new(
            principal.Actor,
            $"authorization.evaluated:{binding.Id}",
            binding.Paths.FirstOrDefault(),
            decision,
            reason,
            null,
            policyVersion,
            JsonSerializer.Serialize(new { roleEval = decision == "allow" ? "match" : "no-match", operationId = binding.Id, ruleCount }));

    /// <summary>
    /// First binding whose method matches and whose route-template patterns
    /// match the resolved endpoint's template (fallback: the request path —
    /// literal segments verbatim, parameters as wildcards).
    /// </summary>
    private static OperationBinding? MatchBinding(IReadOnlyList<OperationBinding> operations, HttpContext context, PathString path)
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
                if ((endpointTemplate is not null && Segmentwise(candidate, endpointTemplate))
                    || Segmentwise(candidate, path.Value ?? string.Empty))
                {
                    return operation;
                }
            }
        }

        return null;
    }

    // Compare segment by segment. Literal segments must match; a `{param}`
    // on either side matches any segment on the other.
    private static bool Segmentwise(string candidate, string other)
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
