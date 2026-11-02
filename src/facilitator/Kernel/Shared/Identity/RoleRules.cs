using System.Text.Json;
using System.Text.Json.Serialization;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// Operator-constructed role definition: a role name plus an OR-list of
/// rules, each rule an AND of claim-requirement conditions evaluated against
/// the verified token payload. Serialized as the <c>rules</c> jsonb column of
/// <c>facilitator.identity_roles</c> (array of rule objects, no name inside —
/// the name is the PK).
/// </summary>
public sealed record RoleRuleSet(string Name, IReadOnlyList<RoleRule> Rules);

/// <summary>One OR-alternative: the rule passes iff ALL conditions pass.</summary>
public sealed record RoleRule(IReadOnlyList<RoleCondition> All);

/// <summary>One claim requirement. Ops are exactly equals | contains | exists.</summary>
public sealed record RoleCondition(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("value")] string? Value);

/// <summary>Wire/persistence shape: <c>[{"all":[{claim,op,value},..]},..]</c>.</summary>
public sealed record RoleRuleDocument(IReadOnlyList<RoleRule> Rules);

/// <summary>
/// Pure, framework-free evaluator (the standards-only core):
/// a role is granted iff any of its rules' conditions ALL pass. Anything
/// malformed — bad jsonb, unknown op, unresolvable non-exists condition —
/// skips that rule (never wildcard-grants).
/// </summary>
public static class RoleEvaluator
{
    /// <summary>Outcome of one evaluation pass.</summary>
    /// <param name="Granted">Role names whose rule-set matched.</param>
    /// <param name="SkippedRules">Conditions/rules skipped as malformed (caller logs).</param>
    public readonly record struct EvaluationOutcome(IReadOnlySet<string> Granted, int SkippedRules);

    /// <summary>Evaluate all rule sets against a verified token payload.</summary>
    public static EvaluationOutcome Evaluate(JsonElement tokenPayload, IReadOnlyList<RoleRuleSet> sets)
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var set in sets)
        {
            var matched = false;
            foreach (var rule in set.Rules)
            {
                if (rule.All is null || rule.All.Count == 0 || rule.All.Any(static c => !RoleConditionOps.IsKnown(c.Op)))
                {
                    skipped++;
                    continue;
                }

                var allPass = true;
                foreach (var condition in rule.All)
                {
                    if (!ConditionPasses(tokenPayload, condition))
                    {
                        allPass = false;
                        break;
                    }
                }

                if (allPass)
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                granted.Add(set.Name);
            }
        }

        return new EvaluationOutcome(granted, skipped);
    }

    private static bool ConditionPasses(JsonElement payload, RoleCondition condition)
    {
        if (condition.Op == RoleConditionOps.ExistsOp)
        {
            return ClaimPointers.TryResolve(payload, condition.Claim, out _);
        }

        if (!ClaimPointers.TryResolve(payload, condition.Claim, out var value))
        {
            return false;
        }

        switch (condition.Op)
        {
            case RoleConditionOps.EqualsOp:
                return value.ValueKind == JsonValueKind.String && value.GetString() == condition.Value;
            case RoleConditionOps.ContainsOp:
                return value.ValueKind switch
                {
                    JsonValueKind.Array => ContainsToken(value, condition.Value),
                    JsonValueKind.String => IsSpaceDelimitedToken(value.GetString()!, condition.Value),
                    _ => false,
                };
            default:
                // Unknown op: malformed rule — skip (deny), never wildcard.
                return false;
        }
    }

    private static bool ContainsToken(JsonElement array, string? value)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSpaceDelimitedToken(string raw, string? value) =>
        value is not null
        && raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(value, StringComparer.Ordinal);
}

/// <summary>jsonb (de)serialization for role rule documents (tolerant reader: bad jsonb ⇒ null ⇒ caller skips + logs).</summary>
public static class RoleRuleJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parse the <c>rules</c> jsonb array; null when malformed.</summary>
    public static IReadOnlyList<RoleRule>? ParseRules(string json)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<List<RoleRule>>(json, Options);
            return rules;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialize rules for the jsonb column / API response.</summary>
    public static string Serialize(IReadOnlyList<RoleRule> rules) =>
        JsonSerializer.Serialize(rules, Options);

    /// <summary>Convert bound config rules to the engine model (config bootstraps the store).</summary>
    public static IReadOnlyList<RoleRuleSet> FromConfig(IdentityOptions options) =>
        options.Roles.Select(static pair => new RoleRuleSet(
            pair.Key,
            (pair.Value ?? []).Select(static rule => new RoleRule(
                rule.All.Select(static c => new RoleCondition(c.Claim, c.Op, c.Value)).ToList())).ToList())).ToList();
}
