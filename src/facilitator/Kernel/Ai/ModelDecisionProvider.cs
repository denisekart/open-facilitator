using System.Globalization;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The stored-accepted <c>routing-defaults.provider = "model"</c> implementation:
/// an ADVISORY preference source over the kernel-computed allowed set. The
/// ladder per call: (a) the <c>llm</c> policy must be live and not
/// <c>off</c>; (b) level 1 — the decisions API, when the policy's decision
/// endpoint is configured and <c>decisionMode != "llm"</c>: one
/// <see cref="ChoiceQuestion"/> (<c>preferred_endpoint</c>) over the candidate
/// ids; an in-set choice with confidence &gt;= 0.5 is the preference, a
/// refusal or off-set answer is no preference; (c) level 2 — when the
/// decisions call is unavailable (<see cref="AiUnavailableException"/>) or
/// forced, the full chat model as a JSON classifier over the
/// <c>decision-routing</c> template. Any null return means "no preference —
/// the deterministic order stands" (audited <c>model-fallback</c> by the
/// routing sink). Non-AI exceptions propagate: only gateway-unavailability
/// degrades.
/// </summary>
public sealed class ModelDecisionProvider(
    AiOptions options,
    IAiGateway gateway,
    IDecisionGateway decisions,
    AiPolicyStore policyStore,
    PromptRenderer renderer) : IDecisionProvider
{
    /// <summary>The routing question name on the decisions API.</summary>
    internal const string QuestionName = "preferred_endpoint";

    /// <summary>The answer key of the <c>decision-routing</c> template's JSON
    /// contract (level 2) — distinct from the decisions-API question name.</summary>
    internal const string ClassifierAnswerKey = "preferred";

    /// <summary>Preference floor below which a model answer is ignored.</summary>
    internal const double ConfidenceFloor = 0.5;

    /// <summary>Stable phrasing for the routing question (level 1).</summary>
    internal const string RoutingInstructions =
        "Choose the single endpoint best suited to execute this work. Answer with exactly one of the listed endpoint ids.";

    /// <inheritdoc />
    public string Name => "model";

    /// <inheritdoc />
    public async Task<DecisionOutcome?> EvaluateAsync(
        DecisionKind kind,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyList<RoutingCandidate> allowedCandidates,
        CancellationToken ct)
    {
        if (kind != DecisionKind.Routing || allowedCandidates.Count == 0)
        {
            return null;
        }

        var policy = await policyStore.LoadAsync(ct);
        if (!policy.Enabled || policy.DecisionMode == "off")
        {
            return null;
        }

        var allowed = allowedCandidates.Select(c => c.EndpointId).ToHashSet(StringComparer.Ordinal);
        var inputText = RenderInput(facts, allowedCandidates);

        // Level 1 — decisions API (real decision model), unless the policy
        // forces the chat-model classifier.
        if (policy.DecisionsApiUsable(options) && policy.DecisionMode != "llm")
        {
            try
            {
                var answers = await decisions.DecideAsync(
                    new DecisionRequest(
                        policy.DecisionEndpoint!,
                        inputText,
                        [new ChoiceQuestion(QuestionName, RoutingInstructions, allowedCandidates.Select(c => c.EndpointId).ToList())]),
                    ct);
                var answer = answers.FirstOrDefault(a => a.Name == QuestionName);
                if (answer?.Choice is { Length: > 0 } choice && allowed.Contains(choice) && answer.Confidence >= ConfidenceFloor)
                {
                    return new DecisionOutcome(choice, "decisions-api preferred_endpoint", answer.Confidence);
                }

                // A delivered answer (refusal, off-set, low confidence) is an
                // informed "no preference" — the deterministic order stands.
                return null;
            }
            catch (AiUnavailableException)
            {
                // Decisions model down/timing out — level 2 takes over.
            }
        }

        // Level 2 — chat-model JSON classifier over the same facts.
        if (options.Endpoint("decoder") is null)
        {
            return null;
        }

        renderer.Render(
            "decision-routing",
            new
            {
                Facts = facts.Select(f => $"{f.Key}: {f.Value}").ToList(),
                Candidates = allowedCandidates.Select(c => new
                {
                    Id = c.EndpointId,
                    Description = $"capability specificity {c.Specificity}; session opened {c.OpenedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}",
                }).ToList(),
            },
            out var system,
            out var user);

        AiCompletion completion;
        try
        {
            completion = await gateway.CompleteAsync(
                new AiRequest("decoder", "decision-routing", system, user, policy.MaxOutputTokens), ct);
        }
        catch (AiUnavailableException)
        {
            return null;
        }

        if (!DecisionJson.TryParse(completion.Text, ClassifierAnswerKey, out var preferred, out var rationale, out var confidence))
        {
            return null;
        }

        return preferred is { Length: > 0 } pick
               && allowed.Contains(pick)
               && confidence >= ConfidenceFloor
            ? new DecisionOutcome(pick, rationale, confidence)
            : null;
    }

    /// <summary>Deterministic plain-text rendering of the intent facts + the
    /// candidate list (same shape feeds both ladder levels).</summary>
    internal static string RenderInput(IReadOnlyDictionary<string, string> facts, IReadOnlyList<RoutingCandidate> candidates)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in facts)
        {
            sb.Append(key).Append(": ").AppendLine(value);
        }

        sb.AppendLine("candidates (deterministic order):");
        foreach (var c in candidates)
        {
            sb.Append("- ").Append(c.EndpointId)
              .Append(" specificity=").Append(c.Specificity)
              .Append(" openedAt=").Append(c.OpenedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>Strict-parse for the level-2 classifier contract
/// <c>{ "&lt;key&gt;": ..., "rationale": ..., "confidence": ... }</c>: slices
/// the outermost braces so fenced code blocks still parse; anything short of
/// a decodable object is "no preference".</summary>
internal static class DecisionJson
{
    /// <summary>Parse a classifier reply; false = unparseable/no answer.</summary>
    internal static bool TryParse(string text, string key, out string? chosen, out string rationale, out double confidence)
    {
        chosen = null;
        rationale = string.Empty;
        confidence = 0;
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[first..(last + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var chosenNode))
            {
                return false;
            }

            if (chosenNode.ValueKind == JsonValueKind.String)
            {
                chosen = chosenNode.GetString();
            }
            else if (chosenNode.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            {
                return false;
            }

            if (root.TryGetProperty("rationale", out var why) && why.ValueKind == JsonValueKind.String)
            {
                rationale = why.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
            {
                confidence = conf.GetDouble();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
