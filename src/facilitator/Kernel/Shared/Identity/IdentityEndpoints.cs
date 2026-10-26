using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// <c>/api/identity/*</c>: the runtime role-rule surface (GET authenticated
/// and ungated; PUT/DELETE gated by <c>identity.roles.manage</c>, config).
/// Endpoints stay thin like <c>WorkEndpoints</c> — parse, delegate to
/// <see cref="RoleStore"/>, map.
/// </summary>
public static class IdentityEndpoints
{
    /// <summary>Map the identity management group.</summary>
    public static IEndpointRouteBuilder MapIdentityApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/identity");

        group.MapGet("/roles", async (RoleStore store, CancellationToken ct) =>
        {
            var rows = await store.ListAsync(ct);
            return Results.Ok(rows
                .Select(static r => new { roleName = r.RoleName, rules = r.Rules, rowVersion = r.RowVersion })
                .ToArray());
        });

        group.MapPut("/roles/{name}", async (string name, HttpRequest request, RoleStore store, HttpContext http, CancellationToken ct) =>
        {
            var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            long? expectedVersion;
            IReadOnlyList<RoleRule>? rules;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                expectedVersion = root.TryGetProperty("rowVersion", out var rv) && rv.TryGetInt64(out var parsed)
                    ? parsed
                    : null;
                rules = root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array
                    ? RoleRuleJson.ParseRules(rulesElement.GetRawText())
                    : null;
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body must be {rules:[{all:[{claim,op,value}]}], rowVersion?}"));
            }

            if (rules is null || rules.Count == 0 || RulesMalformed(rules))
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "rules must be a non-empty OR-list of conditions {claim,op,value} (op: equals|contains|exists, pointers RFC 6901)"));
            }

            var outcome = await store.UpsertAsync(name, rules, expectedVersion, http.GetFacilitatorPrincipal()?.Actor, ct);
            return outcome.Mismatch
                ? Conflict(name)
                : Results.Ok(new { roleName = name, rules, rowVersion = outcome.RowVersion });
        });

        group.MapDelete("/roles/{name}", async (string name, RoleStore store, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await store.RemoveAsync(name, null, http.GetFacilitatorPrincipal()?.Actor, ct);
            return outcome.Stored ? Results.NoContent() : Conflict(name);
        });

        return app;
    }

    private static bool RulesMalformed(IReadOnlyList<RoleRule> rules) =>
        rules.Any(static rule => rule.All is null || rule.All.Count == 0
            || rule.All.Any(static c => !ClaimPointers.IsValidPointer(c.Claim)
                || !RoleConditionOps.IsKnown(c.Op)
                || (c.Op != RoleConditionOps.ExistsOp && string.IsNullOrEmpty(c.Value))));

    private static IResult Conflict(string name) =>
        Results.Problem(ProblemErrors.Create(
            "role-version-conflict", "Role version conflict", StatusCodes.Status409Conflict,
            $"identity role '{name}' changed concurrently (or does not exist) — re-read and retry"));
}
