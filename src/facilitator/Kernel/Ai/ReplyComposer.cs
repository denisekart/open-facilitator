using Facilitator.Kernel.Shared.Persistence;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// Composes one human-facing reply under the actor's verbosity law
/// (<c>reply-composer.scriban</c> over the decoder endpoint). The template
/// mode (<c>llm.replyMode = "template"</c>) never consults a model: the
/// kernel's own deterministic text stands. A model outage
/// (<see cref="AiUnavailableException"/>) propagates — the caller answers
/// with its template reply rather than swallowing the update.
/// </summary>
public sealed class ReplyComposer(IAiGateway gateway, PromptRenderer renderer, AiPolicyStore policyStore)
{
    /// <summary>Composed-text ceiling (prompt-side guard; the token budget
    /// itself is the policy's).</summary>
    public const int MaxTextChars = 4096;

    /// <summary>
    /// Compose the reply for the given facts. Model mode throws
    /// <see cref="AiUnavailableException"/> when the gateway is down (caller
    /// template-falls-back); template mode always returns the deterministic
    /// text and never touches the network.
    /// </summary>
    /// <param name="db">Caller's context (actor preference read).</param>
    /// <param name="policy">Current AiDeck policy.</param>
    /// <param name="actorKey">Canonical actor for the verbosity preference (null ⇒ policy default).</param>
    /// <param name="factsBlock">Kernel-truth lines: the first is the outcome summary, the rest facts.</param>
    /// <param name="platform">Destination surface (e.g. <c>mattermost</c>, <c>github</c>) — tone guidance keys on it; null ⇒ plain text.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> ComposeAsync(
        FacilitatorDbContext db, AiPolicy policy, string? actorKey, string factsBlock, string? platform = null, CancellationToken ct = default)
    {
        var lines = factsBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = lines.Length > 0 ? lines[0] : factsBlock;
        var facts = lines.Length > 1 ? lines[1..] : [];

        if (policy.ReplyMode != "model")
        {
            return Clamp(summary);
        }

        var verbosity = await policyStore.ActorVerbosityAsync(db, actorKey ?? string.Empty, ct)
            ?? policy.VerbosityDefault;
        renderer.Render(
            "reply-composer",
            new { verbosity, facts, intent_summary = summary, platform },
            out var system,
            out var user);
        var completion = await gateway.CompleteAsync(
            new AiRequest("decoder", "reply-composer", system, user, policy.MaxOutputTokens), ct);
        return Clamp(completion.Text.Trim());
    }

    private static string Clamp(string text) => text.Length <= MaxTextChars ? text : text[..MaxTextChars];
}
