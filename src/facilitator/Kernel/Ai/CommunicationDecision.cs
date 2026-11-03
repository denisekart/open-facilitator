using Facilitator.Kernel.Shared;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The communication decision point: when a completion/progress event lands
/// on a bound conversation, should the kernel <c>reply</c>, <c>react</c>, or
/// stay <c>silent</c>. Same advisory ladder as <see cref="ModelDecisionProvider"/>:
/// live policy + decisions API first (one <see cref="ChoiceQuestion"/> named
/// <c>action</c> over the closed set), chat-model JSON classifier over the
/// <c>decision-communication</c> template as level 2, null whenever anything
/// is inert/unavailable/refused/low-confidence — "no preference: the caller's
/// deterministic behavior stands verbatim". Consulted directly by the
/// completion reply site (<c>DecisionKind.Communication</c> exists on the
/// seam's enum, but no routing-style allowed-set funnel consumes it, so this
/// class stands alone rather than as a second <see cref="IDecisionProvider"/>).
/// </summary>
public sealed class CommunicationDecision(
    AiOptions options,
    IAiGateway gateway,
    IDecisionGateway decisions,
    AiPolicyStore policies,
    PromptRenderer renderer)
{
    /// <summary>The communication question name on the decisions API.</summary>
    internal const string QuestionName = "action";

    /// <summary>The closed action set the kernel can express.</summary>
    internal static readonly string[] Actions = ["reply", "react", "silent"];

    /// <summary>Stable phrasing for the communication question (level 1).</summary>
    internal const string ActionInstructions =
        "Decide how the facilitator bot should acknowledge this work outcome in the conversation. Choose reply (send a short text update), react (acknowledge with an emoji only), or silent (say nothing).";

    /// <summary>Advisory answer: "reply" | "react" | "silent", or null = no
    /// preference (the caller's today's behavior stands verbatim). Only
    /// <see cref="AiUnavailableException"/> degrades to null; anything else
    /// propagates.</summary>
    public async Task<string?> DecideAsync(string eventKind, string failureDetail, bool recentConversation, CancellationToken ct)
    {
        var policy = await policies.LoadAsync(ct);
        if (!policy.Enabled || policy.DecisionMode == "off")
        {
            return null;
        }

        var input = RenderInput(eventKind, failureDetail, recentConversation);

        // Level 1 — decisions API.
        if (policy.DecisionsApiUsable(options) && policy.DecisionMode != "llm")
        {
            try
            {
                var answers = await decisions.DecideAsync(
                    new DecisionRequest(policy.DecisionEndpoint!, input, [new ChoiceQuestion(QuestionName, ActionInstructions, Actions)]),
                    ct);
                var answer = answers.FirstOrDefault(a => a.Name == QuestionName);
                if (answer?.Choice is { Length: > 0 } choice && Array.IndexOf(Actions, choice) >= 0 && answer.Confidence >= ModelDecisionProvider.ConfidenceFloor)
                {
                    return choice;
                }

                return null;
            }
            catch (AiUnavailableException)
            {
                // Level 2 takes over.
            }
        }

        // Level 2 — chat-model JSON classifier.
        if (options.Endpoint("decoder") is null)
        {
            return null;
        }

        renderer.Render(
            "decision-communication",
            new
            {
                Facts = FactLines(eventKind, failureDetail, recentConversation),
            },
            out var system,
            out var user);

        AiCompletion completion;
        try
        {
            completion = await gateway.CompleteAsync(
                new AiRequest("decoder", "decision-communication", system, user, policy.MaxOutputTokens), ct);
        }
        catch (AiUnavailableException)
        {
            return null;
        }

        if (!DecisionJson.TryParse(completion.Text, QuestionName, out var action, out _, out var confidence))
        {
            return null;
        }

        return action is { Length: > 0 } pick && Array.IndexOf(Actions, pick) >= 0 && confidence >= ModelDecisionProvider.ConfidenceFloor
            ? pick
            : null;
    }

    /// <summary>Deterministic fact lines (shared by both ladder levels: the
    /// decisions-API input text is the same lines joined).</summary>
    internal static List<string> FactLines(string eventKind, string failureDetail, bool recentConversation) =>
    [
        $"event: {eventKind}",
        $"outcome: {failureDetail}",
        $"human activity in this conversation recently: {(recentConversation ? "yes" : "no")}",
    ];

    /// <summary>Plain-text decisions-API input (deterministic join of the fact lines).</summary>
    internal static string RenderInput(string eventKind, string failureDetail, bool recentConversation) =>
        string.Join('\n', FactLines(eventKind, failureDetail, recentConversation));
}
