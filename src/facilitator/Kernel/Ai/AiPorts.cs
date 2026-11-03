namespace Facilitator.Kernel.Ai;

/// <summary>Any AI-port failure that must degrade (timeout, connect error,
/// 5xx, malformed response). Callers treat it as "AI unavailable", never as a
/// business error: no AI flow may fail a kernel operation with this.</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>One chat-completion request against a named chat endpoint.</summary>
/// <param name="EndpointName">Config endpoint (e.g. <c>decoder</c>).</param>
/// <param name="TemplateId">Prompt template id — recorded in ledger + spans.</param>
/// <param name="SystemPrompt">Rendered system message.</param>
/// <param name="UserPrompt">Rendered user message.</param>
/// <param name="MaxOutputTokens">Output budget.</param>
public sealed record AiRequest(
    string EndpointName,
    string TemplateId,
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens);

/// <summary>One chat-completion response: assistant text + token usage.</summary>
public sealed record AiCompletion(string Text, int PromptTokens, int CompletionTokens);

/// <summary>One <c>/v1/decisions</c> request: state to judge + named questions.</summary>
/// <param name="EndpointName">Config endpoint (e.g. <c>decision</c>).</param>
/// <param name="InputText">The state being judged, rendered as plain text.</param>
/// <param name="Questions">At most 32, uniquely named.</param>
public sealed record DecisionRequest(
    string EndpointName,
    string InputText,
    IReadOnlyList<DecisionQuestion> Questions);

/// <summary>A typed decision question (OpenAI Decisions format).</summary>
public abstract record DecisionQuestion(string Name, string Instructions);

/// <summary>Boolean question answered as a probability.</summary>
public sealed record PredicateQuestion(string Name, string Instructions) : DecisionQuestion(Name, Instructions);

/// <summary>Single-select question over a closed choice set.</summary>
public sealed record ChoiceQuestion(
    string Name, string Instructions, IReadOnlyList<string> Choices) : DecisionQuestion(Name, Instructions);

/// <summary>Answer to one question. A refusal maps to an all-null row
/// (no-preference): <see cref="Predicate"/> and <see cref="Choice"/> both null.</summary>
public sealed record DecisionAnswer(string Name, bool? Predicate, string? Choice, double Confidence);

/// <summary>Port for the LiteLLM decisions API. Throws
/// <see cref="AiUnavailableException"/> on timeout/connect/5xx — never
/// returns garbage, never throws anything else on provider trouble.</summary>
public interface IDecisionGateway
{
    Task<IReadOnlyList<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct);
}

/// <summary>Port for chat completions (the decoder / classifier / composer).
/// Throws <see cref="AiUnavailableException"/> on timeout/connect/5xx.</summary>
public interface IAiGateway
{
    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct);
}
