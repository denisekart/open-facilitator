using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>One chat action as returned to the client (proposal + lifecycle
/// status).</summary>
/// <param name="ActionId">Row id (confirm/cancel target).</param>
/// <param name="Method">Proposed HTTP method.</param>
/// <param name="Path">Proposed route path (catalog-validated).</param>
/// <param name="Body">Proposed request body, raw jsonb — null when the action carries none.</param>
/// <param name="Description">Machine-readable description shown on the action card.</param>
/// <param name="Status">pending | executed | failed | cancelled.</param>
public sealed record ChatActionView(Guid ActionId, string Method, string Path, JsonElement? Body, string Description, string Status);

/// <summary>One completed chat turn: the stored assistant message + its
/// proposed (NOT executed) actions.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="MessageId">Stored assistant message id.</param>
/// <param name="Reply">Assistant text.</param>
/// <param name="Actions">Proposed actions (empty on pure-reply turns and every degraded turn).</param>
public sealed record ChatTurnResult(Guid SessionId, Guid MessageId, string Reply, IReadOnlyList<ChatActionView> Actions);

/// <summary>
/// The admin-chat turn engine. Message flow (one tx with the effect):
/// store the user message (basis <c>slash</c> on a recognized registry verb,
/// else the turn's outcome basis) → load hot policy + the merged command
/// registry → policy inert / replyMode template / gateway down ⇒ honest
/// canned fallback listing the registry verbs; otherwise render the
/// <c>chat</c> template (system: role law + command list + the
/// <see cref="ActionCatalog"/>; user: last 10 stored messages + the new one)
/// → the <c>decoder</c> endpoint → a strict <c>{reply, actions[]}</c> JSON
/// contract. EVERY proposed path must be catalog-allow-listed or the WHOLE
/// turn degrades to a plain fallback reply — the model may only select from
/// the catalog, never invent endpoints. The assistant message,
/// <c>pending</c> action rows, the <c>chat.message</c> audit row and the
/// out-direction ledger append commit together; nothing executes until an
/// admin confirms (<see cref="ChatActionExecutor"/>).
/// </summary>
public sealed class ChatProcessor(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AiPolicyStore policyStore,
    IAiGateway gateway,
    PromptRenderer renderer,
    AuditWriter audit,
    KernelMetrics metrics,
    TimeProvider timeProvider)
{
    /// <summary>Title seed on session create; replaced by the first user turn (truncated).</summary>
    public const string ProvisionalTitle = "New conversation";

    /// <summary>Entity column cap on session titles; the law truncates the first turn to 80.</summary>
    public const int TitleLimit = 80;

    private const string CatalogDegradeReply = "I couldn't map that to an action — every proposal must match a known kernel endpoint. Rephrase, or use the admin surface directly.";

    /// <summary>Create an empty session owned by the principal; the title
    /// becomes the first user turn (truncated to <see cref="TitleLimit"/>).</summary>
    public async Task<ChatSessionEntity> CreateSessionAsync(string actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = new ChatSessionEntity
        {
            SessionId = Guid.NewGuid(),
            Actor = actor,
            Title = ProvisionalTitle,
            CreatedAt = now,
            LastMessageAt = now,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Sessions newest-activity-first; <paramref name="all"/> lists
    /// every owner (the caller has verified <c>chat.read-all</c>).</summary>
    public async Task<List<ChatSessionEntity>> ListSessionsAsync(string actor, bool all, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var query = db.ChatSessions.AsNoTracking();
        if (!all)
        {
            query = query.Where(s => s.Actor == actor);
        }

        return await query
            .OrderByDescending(s => s.LastMessageAt).ThenByDescending(s => s.SessionId)
            .Take(200)
            .ToListAsync(ct);
    }

    /// <summary>Stored history, oldest-first.</summary>
    public async Task<List<ChatMessageEntity>> MessagesAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId)
            .ToListAsync(ct);
    }

    /// <summary>One action row (with its owning session for the access check), or null.</summary>
    public async Task<(ChatActionEntity Action, ChatSessionEntity Session)?> LoadActionAsync(Guid actionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.ChatActions.AsNoTracking()
            .Where(a => a.ActionId == actionId)
            .Join(db.ChatSessions.AsNoTracking(), a => a.SessionId, s => s.SessionId, (a, s) => new { Action = a, Session = s })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Action, row.Session);
    }

    /// <summary>
    /// Run one chat turn. The caller has verified the session exists and the
    /// principal may post to it (owner or <c>chat.read-all</c>).
    /// </summary>
    public async Task<ChatTurnResult> PostMessageAsync(
        Guid sessionId, string text, FacilitatorPrincipal principal, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var traceId = Activity.Current?.TraceId.ToString();

        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw new InvalidOperationException($"chat session {sessionId} vanished");

        // Single source of truth for commands: built-ins + the admin_config row.
        var registry = CommandRegistry.BuiltIns;
        var commandsRow = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "commands")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (commandsRow is { Length: > 0 })
        {
            try
            {
                registry = CommandRegistry.Merge(registry, JsonDocument.Parse(commandsRow).RootElement);
            }
            catch (JsonException)
            {
                // Junk registry row ⇒ built-ins only (the registry never fails closed).
            }
        }

        var slash = CommandParser.TryParse(text, registry);
        var slashKnown = slash is { Unknown: false };
        var policy = await policyStore.LoadAsync(db, ct);

        string reply;
        string basis;
        string? templateId = null;
        JsonElement? decoded = null;
        int? promptTokens = null;
        int? completionTokens = null;
        List<PendingAction> proposals = [];

        if (!slashKnown && policy.Enabled && policy.ReplyMode == "model")
        {
            var outcome = await ModelTurnAsync(db, sessionId, text, registry, policy, ct);
            reply = outcome.Reply;
            basis = outcome.Basis;
            templateId = "chat";
            decoded = outcome.Decoded;
            promptTokens = outcome.PromptTokens;
            completionTokens = outcome.CompletionTokens;
            proposals = outcome.Proposals;
        }
        else
        {
            reply = FallbackReply(registry);
            basis = "fallback";
        }

        if (session.Title == ProvisionalTitle)
        {
            session.Title = text.Length <= TitleLimit ? text : text[..TitleLimit];
        }

        session.LastMessageAt = now;
        db.ChatMessages.Add(new ChatMessageEntity
        {
            MessageId = Guid.NewGuid(),
            SessionId = sessionId,
            Role = "user",
            Text = text,
            Basis = slashKnown ? "slash" : basis,
            CreatedAt = now,
            TraceId = traceId,
        });
        var assistantId = Guid.NewGuid();
        db.ChatMessages.Add(new ChatMessageEntity
        {
            MessageId = assistantId,
            SessionId = sessionId,
            Role = "assistant",
            Text = reply,
            Basis = basis,
            TemplateId = templateId,
            CreatedAt = now,
            TraceId = traceId,
        });

        var actions = new List<ChatActionView>(proposals.Count);
        foreach (var proposal in proposals)
        {
            var actionId = Guid.NewGuid();
            db.ChatActions.Add(new ChatActionEntity
            {
                ActionId = actionId,
                SessionId = sessionId,
                MessageId = assistantId,
                Method = proposal.Method,
                Path = proposal.Path,
                Body = proposal.Body,
                Description = proposal.Description,
                Status = "pending",
                CreatedAt = now,
            });
            actions.Add(new ChatActionView(actionId, proposal.Method, proposal.Path,
                proposal.Body is { } bodyText ? JsonDocument.Parse(bodyText).RootElement.Clone() : null,
                proposal.Description, "pending"));
        }

        audit.Append(db, new AuditRecord(
            principal.Actor, "chat.message", $"/api/chat/sessions/{sessionId}", "allow", null, null, null,
            JsonSerializer.Serialize(new
            {
                sessionId,
                basis,
                actions = proposals.Count,
                note = basis == "fallback" && !slashKnown ? "couldn't map to an action" : null,
            })));

        AiLedgerStore.Append(db, new AiTurnAppend(
            AiLedgerStore.ChatKey(sessionId), principal.Actor, null, "out", reply, basis,
            templateId, decoded, "decoder", promptTokens, completionTokens, traceId), now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.AiChatMessage(basis);
        return new ChatTurnResult(sessionId, assistantId, reply, actions);
    }

    // ---- model turn ----

    private sealed record ModelOutcome(
        string Reply,
        string Basis,
        JsonElement? Decoded,
        int? PromptTokens,
        int? CompletionTokens,
        List<PendingAction> Proposals);

    private sealed record PendingAction(string Method, string Path, string? Body, string Description);

    private async Task<ModelOutcome> ModelTurnAsync(
        FacilitatorDbContext db,
        Guid sessionId,
        string text,
        CommandRegistry registry,
        AiPolicy policy,
        CancellationToken ct)
    {
        var history = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(10)
            .ToListAsync(ct);
        history.Reverse();

        renderer.Render(
            "chat",
            new
            {
                Commands = registry.All().Select(s => new { s.Verb, s.Description, Args = s.Args.Select(a => new { a.Name, a.Required }).ToList() }).ToList(),
                Catalog = ActionCatalog.Entries.Select(e => new { e.Method, Path = e.PathTemplate, e.Summary, e.BodyHint, e.Notes }).ToList(),
                Messages = history.Select(m => new { Role = m.Role, Text = m.Text }).ToList(),
                Message = text,
            },
            out var system,
            out var user);

        using var span = KernelActivity.StartAiChatTurnSpan("decoder", "chat");
        AiCompletion completion;
        try
        {
            completion = await gateway.CompleteAsync(
                new AiRequest("decoder", "chat", system, user, policy.MaxOutputTokens), ct);
        }
        catch (AiUnavailableException)
        {
            metrics.AiGatewayError("decoder");
            return new ModelOutcome(
                "AI unavailable — the chat plane stays usable: commands and the admin surface work without it.",
                "fallback", null, null, null, []);
        }

        KernelActivity.SetAiTokenUsage(span, completion.PromptTokens, completion.CompletionTokens);

        var parsed = TryParseTurn(completion.Text);
        if (parsed is null)
        {
            return new ModelOutcome(FallbackReply(CommandRegistry.BuiltIns), "fallback", null, completion.PromptTokens, completion.CompletionTokens, []);
        }

        var (root, decoded) = parsed.Value;
        var reply = root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? string.Empty
            : string.Empty;

        var proposals = new List<PendingAction>();
        if (root.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in arr.EnumerateArray())
            {
                var proposal = TryReadAction(candidate);
                if (proposal is null || !ActionCatalog.Matches(proposal.Method, proposal.Path))
                {
                    // Whole-turn degradation: one out-of-catalog proposal voids
                    // every proposal — partial execution of a half-trusted turn
                    // is worse than none.
                    return new ModelOutcome(CatalogDegradeReply, "fallback", decoded, completion.PromptTokens, completion.CompletionTokens, []);
                }

                proposals.Add(proposal);
            }
        }

        if (reply.Length == 0 && proposals.Count == 0)
        {
            reply = CatalogDegradeReply;
        }

        return new ModelOutcome(reply, "model", decoded, completion.PromptTokens, completion.CompletionTokens, proposals);
    }

    // Strict {reply, actions} parse, tolerant of a fenced code block. Null on
    // any structural violation (the caller degrades the turn).
    private static (JsonElement Root, JsonElement Decoded)? TryParseTurn(string text)
    {
        var payload = text.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = payload.IndexOf('\n');
            if (firstBreak < 0)
            {
                return null;
            }

            payload = payload[(firstBreak + 1)..];
            var fenceEnd = payload.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                payload = payload[..fenceEnd];
            }

            payload = payload.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("actions", out var actions)
                || actions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return (root, root.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PendingAction? TryReadAction(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object
            || candidate.GetPropertyText("method") is not { Length: > 0 } method
            || candidate.GetPropertyText("path") is not { Length: > 0 } path
            || candidate.GetPropertyText("description") is not { Length: > 0 } description)
        {
            return null;
        }

        string? body = null;
        if (candidate.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            body = bodyElement.GetRawText();
        }

        return new PendingAction(method.ToUpperInvariant(), path, body, Truncate(description, 2000));
    }

    // ---- fallback (zero-AI honest reply) ----

    private static string RenderCommandList(CommandRegistry registry)
    {
        var builder = new StringBuilder();
        foreach (var spec in registry.All())
        {
            builder.Append("- /").Append(spec.Verb);
            foreach (var arg in spec.Args)
            {
                builder.Append(arg.Required ? " <" : " [")
                    .Append(arg.Name)
                    .Append(arg.Required ? '>' : ']');
            }

            builder.Append(" — ").Append(spec.Description).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string FallbackReply(CommandRegistry registry) =>
        "Commands applied via the chat plane are executed as proposals — nothing runs until you confirm on the admin surface. "
        + "Available commands:\n" + RenderCommandList(registry);

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];
}

file static class JsonElementExtensions
{
    internal static string? GetPropertyText(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
