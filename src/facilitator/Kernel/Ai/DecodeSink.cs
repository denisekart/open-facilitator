using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;


namespace Facilitator.Kernel.Ai;

/// <summary>
/// The async decode plane (channel <c>decode</c>): one outbox row per live
/// AiDeck non-slash mention (<c>org.facilitator.kernel.decode.requested.v1</c>,
/// internal payload, crash-replay idempotent via the deterministic
/// <c>decode:&lt;deliveryId&gt;</c> command id). Per row: policy gate →
/// context → <c>decode</c> template → gateway → strict contract parse → the
/// <c>model</c> may only SELECT a registry verb, executed through the same
/// <see cref="CommandApplier"/> as the slash plane. Every non-selection
/// (junk, low confidence, unknown, chat-without-reply, gateway outage inside
/// the attempt budget's tail) degrades to the verbatim-Work fallback with the
/// no-AI hint — an AI outage never DLQs a human message.
/// </summary>
public sealed class DecodeSink(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AiPolicyStore policyStore,
    AiOptions aiOptions,
    IAiGateway gateway,
    PromptRenderer renderer,
    CommandApplier applier,
    ReplyComposer composer,
    DecodeContextBuilder contextBuilder,
    ProviderRegistry providerRegistry,
    KernelMetrics metrics,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : ISink
{
    /// <inheritdoc />
    public string Channel => "decode";

    /// <summary>Envelope type of a decode request row (internal channel; like
    /// the kernel.log channel this payload carries no wire schema, so the row
    /// is inserted without EnvelopeValidator — every outbound reply/react/
    /// progress row this sink emits IS validator-gated).</summary>
    public const string RequestType = "org.facilitator.kernel.decode.requested.v1";

    /// <summary>Gateway attempts before the degraded fallback (poison-drain
    /// law: two honest tries, then the human answer that needs no AI).</summary>
    public const int FallbackAfterAttempts = 2;

    /// <summary>Decode contract floor: a selection below this confidence
    /// falls back rather than acting on a guess.</summary>
    public const double MinConfidence = 0.5;

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
    {
        JsonElement data;
        string text;
        ProviderEventStore.Reference reference;
        string actor;
        string deliveryId;
        Guid teamGuid;
        try
        {
            data = row.Envelope.GetProperty("data");
            text = data.GetProperty("text").GetString() ?? string.Empty;
            reference = ReferenceOf(data.GetProperty("reference"));
            actor = data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty;
            deliveryId = data.GetProperty("providerDeliveryId").GetString()
                ?? throw new InvalidOperationException("decode row without providerDeliveryId");
            teamGuid = Guid.Parse(data.GetProperty("teamId").GetString()
                ?? throw new InvalidOperationException("decode row without teamId"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            _ = e;
            // A malformed internal row is a kernel bug, not an AI outage —
            // dead-letter it (return false lets the dispatcher try twice
            // more, then it drains like any poison).
            metrics.AiDecode("malformed");
            return false;
        }

        if (!providerRegistry.TryGet(reference.Provider, out var options))
        {
            metrics.AiDecode("malformed");
            return false;
        }

        // Chat label routing + inbound media (both optional on an internal
        // row): the row's own `labels` go through the exact matcher the
        // synchronous arms use (AppendDecodeRow writes both fields; a row
        // without them behaves exactly as before — no rule, "[]" refs).
        var chatRule = ProviderEventStore.MatchChatLabelRule(options, data);
        var media = ProviderEventStore.PayloadSourceRefs(data);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var policy = await policyStore.LoadAsync(ct);
        if (!policy.Enabled || !aiOptions.HasEndpoints)
        {
            // Policy went inert between enqueue and delivery: honest
            // degradation, today's verbatim behavior + hint.
            return await FallbackAsync(text, reference, options, actor, teamGuid, deliveryId, ct, chatRule, media);
        }

        var registry = await MergedRegistryAsync(ct);
        var conversationKey = AiLedgerStore.ProviderKey(reference.Provider, reference.Channel, reference.Root);
        var ack = data.TryGetProperty("replyVsReaction", out var rvr) && rvr.GetString() == "reply"
            ? "reply" : "reaction";

        await using (var contextDb = await contextFactory.CreateDbContextAsync(ct))
        {
            var context = await contextBuilder.BuildAsync(
                contextDb, conversationKey, actorKey: null, reference.Tenant, teamGuid, text, ct);
            renderer.Render(
                "decode",
                new
                {
                    commands = registry.All().Select(s => new { s.Verb, s.Description, Args = s.Args.Select(a => new { a.Name, a.Required }).ToList() }).ToList(),
                    context = context.RenderedBlock,
                    message = text,
                },
                out var system,
                out var user);

            AiCompletion completion;
            using (var span = KernelActivity.StartAiDecodeSpan("decoder", "decode"))
            {
                try
                {
                    completion = await gateway.CompleteAsync(
                        new AiRequest("decoder", "decode", system, user, policy.MaxOutputTokens), ct);
                    KernelActivity.SetAiTokenUsage(span, completion.PromptTokens, completion.CompletionTokens);
                }
                catch (AiUnavailableException)
                {
                    metrics.AiGatewayError("decoder");
                    if (row.Attempts >= FallbackAfterAttempts)
                    {
                        return await FallbackAsync(text, reference, options, actor, teamGuid, deliveryId, ct, chatRule, media);
                    }

                    // Transient: requeue with dispatcher backoff — never a
                    // silent drop, never a DLQ of a human message.
                    return false;
                }
            }

            var decoded = TryParseDecoded(completion.Text);
            if (decoded is null)
            {
                metrics.AiDecode("junk");
                return await FallbackAsync(text, reference, options, actor, teamGuid, deliveryId, ct, chatRule, media);
            }

            var (intent, verb, args, reply, confidence) = decoded.Value;
            var decodedDocument = DecodeContract(completion.Text);
            var intentCommand = intent == "command"
                && verb is { Length: > 0 } v && registry.TryGet(v, out _)
                && confidence >= MinConfidence;

            if (intentCommand)
            {
                return await ApplyDecodedAsync(
                    completion, decodedDocument, verb!, args, reply, text, reference, options, actor,
                    teamGuid, deliveryId, ack, policy, registry, conversationKey, now, ct, chatRule, media);
            }

            if (intent == "chat")
            {
                return await ChatReplyAsync(
                    completion, decodedDocument, reply, text, reference, options, actor,
                    deliveryId, policy, conversationKey, now, ct);
            }

            metrics.AiDecode("unknown");
            return await FallbackAsync(text, reference, options, actor, teamGuid, deliveryId, ct, chatRule, media);
        }
    }

    // ---- effect paths --------------------------------------------------------

    private async Task<bool> ApplyDecodedAsync(
        AiCompletion completion, JsonElement? decodedDocument, string verb, IReadOnlyDictionary<string, string> args, string? clarification,
        string text, ProviderEventStore.Reference reference, ProviderOptions options, string actor,
        Guid teamGuid, string deliveryId, string ack, AiPolicy policy, CommandRegistry registry,
        string conversationKey, DateTime now, CancellationToken ct,
        AssignRule? rule = null, JsonArray? sourceRefs = null)
    {
        // Crash-replay guard: the reply row of a prior (committed, died-after)
        // pass means the effect already stands.
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (await db.Outbox.AsNoTracking().AnyAsync(o => o.CommandId == $"cmd:{verb}:{deliveryId}", ct))
        {
            return true;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var cmd = new ParsedCommand(verb, args, clarification ?? text, false);
        var effect = await applier.ApplyAsync(
            db, now, cmd, registry, reference, options, actor, teamGuid, deliveryId, ack, policy, ct,
            ledgerBasis: "model", rule: rule, sourceRefs: sourceRefs);
        StampLedger(db, conversationKey, actor, completion, now, decodedDocument, text);
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, effect.WorkId?.ToString(), ct);
        await tx.CommitAsync(ct);
        if (effect.PostCommit is { } post)
        {
            await post(scopeFactory.CreateScope().ServiceProvider);
        }

        return true;
    }

    private async Task<bool> ChatReplyAsync(
        AiCompletion completion, JsonElement? decodedDocument, string? reply, string text, ProviderEventStore.Reference reference,
        ProviderOptions options, string actor, string deliveryId, AiPolicy policy,
        string conversationKey, DateTime now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var key = $"cmd:chat:{deliveryId}";
        if (await db.Outbox.AsNoTracking().AnyAsync(o => o.CommandId == key, ct))
        {
            return true;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var facts = reply is { Length: > 0 } r ? r : $"Someone said: {text}";
        string composed;
        try
        {
            composed = await composer.ComposeAsync(db, policy, actorKey: null, facts, reference.Provider, ct);
        }
        catch (AiUnavailableException)
        {
            metrics.AiGatewayError("decoder");
            composed = reply is { Length: > 0 } fallbackReply ? fallbackReply : "Noted.";
        }

        applier.AppendCommandRow(db, ProviderEventStore.ReplyEnvelope(
            ProviderEventStore.TargetOf(reference), composed, key,
            $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now), options, now, workId: null);
        StampLedger(db, conversationKey, actor, completion, now, decodedDocument, text);
        AiLedgerStore.Append(db, new AiTurnAppend(
            conversationKey, Actor: null, ProviderAccountId: actor, Direction: "out",
            Text: composed, Basis: "model", TemplateId: "reply-composer",
            EndpointName: "decoder"), now);
        metrics.AiDecode("chat");
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, payload: null, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<bool> FallbackAsync(
        string text, ProviderEventStore.Reference reference, ProviderOptions options, string actor,
        Guid teamGuid, string deliveryId, CancellationToken ct,
        AssignRule? rule = null, JsonArray? sourceRefs = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // Replay guard: the ack row of a prior pass means the fallback Work stands.
        if (await db.Outbox.AsNoTracking().AnyAsync(o => o.CommandId == $"mention:{deliveryId}:ack", ct))
        {
            return true;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var effect = await applier.VerbatimWorkAsync(
            db, now, text, reference, options, actor, teamGuid, deliveryId,
            await ReplyVsReactionAsync(db, ct), ct, rule, sourceRefs);
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, effect.WorkId?.ToString(), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>The ledger 'in' row for the model pass: the message the
    /// decoder consumed, the parsed contract document, endpoint + token
    /// usage — the audit of exactly what the model saw and decided.</summary>
    private static void StampLedger(
        FacilitatorDbContext db, string conversationKey, string actor, AiCompletion completion,
        DateTime now, JsonElement? decodedDocument, string messageText)
    {
        AiLedgerStore.Append(db, new AiTurnAppend(
            conversationKey, Actor: null, ProviderAccountId: actor, Direction: "in",
            Text: messageText, Basis: "model", TemplateId: "decode",
            Decoded: decodedDocument, EndpointName: "decoder",
            PromptTokens: completion.PromptTokens, CompletionTokens: completion.CompletionTokens), now);
    }

    private async Task<CommandRegistry> MergedRegistryAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return CommandRegistry.Merge(
            CommandRegistry.BuiltIns, await ProviderEventStore.CommandsPayloadAsync(db, ct));
    }

    private static async Task<string> ReplyVsReactionAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "notifications")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is { Length: > 0 } text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("replyVsReaction", out var v) && v.GetString() is "reply" or "reaction")
                {
                    return v.GetString()!;
                }
            }
            catch (JsonException)
            {
                // fall through to the default
            }
        }

        return "reaction";
    }

    /// <summary>Strict contract parse: tolerate a fenced code block by
    /// slicing the outermost braces; anything that is not a single contract
    /// object is junk (null).</summary>
    internal static (string Intent, string? Verb, IReadOnlyDictionary<string, string> Args, string? Reply, double Confidence)?
        TryParseDecoded(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("intent", out var intentEl)
                || intentEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var intent = intentEl.GetString()!;
            var verb = root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in argsEl.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        args[property.Name] = property.Value.GetString()!;
                    }
                }
            }

            var reply = root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            var confidence = root.TryGetProperty("confidence", out var f)
                && f.ValueKind is JsonValueKind.Number && f.TryGetDouble(out var d)
                ? Math.Clamp(d, 0, 1)
                : 0.0;
            return (intent, verb, args, reply, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The contract document re-parsed for jsonb storage (null when
    /// the text was not a single contract object — the junk path never
    /// reaches the ledger stamp anyway).</summary>
    private static JsonElement? DecodeContract(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(trimmed[start..(end + 1)]).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderEventStore.Reference ReferenceOf(JsonElement reference)
    {
        var channel = reference.TryGetProperty("channelOrResourceId", out var ch) ? ch.GetString() ?? string.Empty : string.Empty;
        var kind = reference.TryGetProperty("conversationKind", out var k) ? k.GetString() ?? "thread" : "thread";
        var root = reference.TryGetProperty("rootThreadId", out var r) ? r.GetString() : null;
        var messageId = reference.TryGetProperty("messageId", out var m) ? m.GetString()
            : reference.TryGetProperty("parentMessageId", out var pm) ? pm.GetString() : null;
        var tenant = reference.TryGetProperty("tenantOrOrganizationId", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var provider = reference.GetProperty("provider").GetString() ?? string.Empty;
        return new ProviderEventStore.Reference(provider, tenant, channel, root ?? messageId ?? channel, messageId, kind);
    }
}
