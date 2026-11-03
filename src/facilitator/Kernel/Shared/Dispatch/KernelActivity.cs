using System.Diagnostics;

namespace Facilitator.Kernel.Shared.Dispatch;

/// <summary>Activity source for kernel spans (<c>Facilitator.Kernel</c>).</summary>
public static class KernelActivity
{
    /// <summary>Source name (locked contract).</summary>
    public const string SourceName = "Facilitator.Kernel";

    /// <summary>The kernel activity source.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Start an <c>ai.decode</c> span (decode-pipeline turn through the AI gateway).
    /// Same start-with-parent style as every other kernel span: the caller passes the
    /// extracted <see cref="ActivityContext"/> across a durable hop (or the default for
    /// ambient propagation); returns <c>null</c> when nobody is listening.
    /// </summary>
    /// <param name="endpoint">Configured endpoint name (e.g. decoder), tagged as <c>facilitator.ai.endpoint</c>.</param>
    /// <param name="templateId">Prompt template id, tagged as <c>facilitator.ai.template</c>.</param>
    /// <param name="parent">Explicit parent context (extracted carrier); default = ambient.</param>
    public static Activity? StartAiDecodeSpan(string endpoint, string templateId, ActivityContext parent = default)
    {
        var span = Source.StartActivity("ai.decode", ActivityKind.Internal, parent);
        span?.SetTag("facilitator.ai.endpoint", endpoint);
        span?.SetTag("facilitator.ai.template", templateId);
        return span;
    }

    /// <summary>
    /// Start an <c>ai.chat.turn</c> span (one admin-chat assistant turn). Start-with-parent
    /// semantics identical to <see cref="StartAiDecodeSpan"/>.
    /// </summary>
    /// <param name="endpoint">Configured endpoint name (e.g. decoder), tagged as <c>facilitator.ai.endpoint</c>.</param>
    /// <param name="templateId">Prompt template id, tagged as <c>facilitator.ai.template</c>.</param>
    /// <param name="parent">Explicit parent context (extracted carrier); default = ambient.</param>
    public static Activity? StartAiChatTurnSpan(string endpoint, string templateId, ActivityContext parent = default)
    {
        var span = Source.StartActivity("ai.chat.turn", ActivityKind.Internal, parent);
        span?.SetTag("facilitator.ai.endpoint", endpoint);
        span?.SetTag("facilitator.ai.template", templateId);
        return span;
    }

    /// <summary>
    /// Start an <c>ai.action.execute</c> span (loopback execution of a confirmed chat action).
    /// Start-with-parent semantics identical to <see cref="StartAiDecodeSpan"/>.
    /// </summary>
    /// <param name="endpoint">Configured endpoint name the action was minted against, tagged as <c>facilitator.ai.endpoint</c>.</param>
    /// <param name="parent">Explicit parent context (extracted carrier); default = ambient.</param>
    public static Activity? StartAiActionExecuteSpan(string endpoint, ActivityContext parent = default)
    {
        var span = Source.StartActivity("ai.action.execute", ActivityKind.Internal, parent);
        span?.SetTag("facilitator.ai.endpoint", endpoint);
        return span;
    }

    /// <summary>Stamp gateway token usage onto an AI span (no-op on a null span).</summary>
    /// <param name="span">Span from one of the AI span helpers (may be null when unlistened).</param>
    /// <param name="promptTokens">Prompt-side token count reported by the gateway.</param>
    /// <param name="completionTokens">Completion-side token count reported by the gateway.</param>
    public static void SetAiTokenUsage(Activity? span, int promptTokens, int completionTokens)
    {
        span?.SetTag("facilitator.ai.prompt_tokens", promptTokens);
        span?.SetTag("facilitator.ai.completion_tokens", completionTokens);
    }
}
