using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>Session row on the chat surface.</summary>
/// <param name="SessionId">Session id.</param>
/// <param name="Actor">Owner (canonical oidc actor).</param>
/// <param name="Title">First user turn, truncated — or the provisional seed.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="LastMessageAt">Last activity (list ordering key).</param>
public sealed record ChatSessionView(Guid SessionId, string Actor, string Title, DateTime CreatedAt, DateTime LastMessageAt);

/// <summary>Stored message + its proposed actions (history browsing shows
/// action lifecycle without a per-action poll).</summary>
/// <param name="MessageId">Message id.</param>
/// <param name="Role">user | assistant.</param>
/// <param name="Text">Message text.</param>
/// <param name="Basis">model | fallback | slash.</param>
/// <param name="TemplateId">Prompt template id on model-basis turns.</param>
/// <param name="CreatedAt">Timestamp.</param>
/// <param name="Actions">Proposed actions attached to this (assistant) message.</param>
public sealed record ChatMessageView(
    Guid MessageId,
    string Role,
    string Text,
    string Basis,
    string? TemplateId,
    DateTime CreatedAt,
    IReadOnlyList<ChatActionView> Actions);

/// <summary>One action row on the chat surface (full lifecycle view).</summary>
/// <param name="ActionId">Action id.</param>
/// <param name="SessionId">Owning session.</param>
/// <param name="Method">Proposed method.</param>
/// <param name="Path">Proposed path.</param>
/// <param name="Body">Proposed body (raw jsonb) or null.</param>
/// <param name="Description">Action-card description.</param>
/// <param name="Status">pending | executed | failed | cancelled.</param>
/// <param name="StatusCode">Loopback response status once executed.</param>
/// <param name="Result">Recorded response (truncated 8 KiB) once executed.</param>
/// <param name="ExecutedBy">Confirming actor.</param>
/// <param name="ExecutedAt">Terminal timestamp.</param>
public sealed record ChatActionRowView(
    Guid ActionId,
    Guid SessionId,
    string Method,
    string Path,
    JsonElement? Body,
    string Description,
    string Status,
    int? StatusCode,
    string? Result,
    string? ExecutedBy,
    DateTime? ExecutedAt);

/// <summary>POST body: one user turn.</summary>
/// <param name="Text">Message text (non-empty).</param>
public sealed record ChatMessageRequest(string? Text);

/// <summary>PUT body: verbosity preference.</summary>
/// <param name="Verbosity">terse | balanced | proactive.</param>
public sealed record ChatPreferencesRequest(string? Verbosity);

/// <summary>
/// <c>/api/chat/*</c>: the AiDeck admin chat surface — sessions, stored
/// history, model-proposed actions with confirm/cancel, self-service
/// verbosity preferences. Thin endpoints: parse, delegate to
/// <see cref="ChatProcessor"/>/<see cref="ChatActionExecutor"/>, map.
/// Authorization is config-driven (the <c>chat.use</c>/<c>chat.execute</c>/
/// <c>chat.read-all</c> bindings in <c>identity:authorization:operations</c>
/// — NO <c>.RequireAuthorization</c> anywhere); cross-principal reads add a
/// manual ownership or <c>chat.read-all</c> check inside the handler, the
/// same idiom the gate itself uses on <c>principal.Roles</c>.
/// </summary>
public static class ChatEndpoints
{
    private const string ReadAllRole = "chat.read-all";

    /// <summary>Map the chat group (mount after <c>MapAdminApi</c>).</summary>
    public static IEndpointRouteBuilder MapChatApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat");

        group.MapPost("/sessions", async (ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            var session = await processor.CreateSessionAsync(principal.Actor, ct);
            return Results.Ok(View(session));
        });

        group.MapGet("/sessions", async (bool? all, ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            // Listing OTHERS' sessions is the read-all power; no flag (or a
            // principal without the role) narrows to own.
            var listAll = all == true;
            if (listAll && !principal.Roles.Contains(ReadAllRole))
            {
                return Results.Problem(IdentityAuthentication.Forbidden(ReadAllRole, [ReadAllRole]));
            }

            var sessions = await processor.ListSessionsAsync(principal.Actor, listAll, ct);
            return Results.Ok(sessions.Select(View).ToArray());
        });

        group.MapGet("/sessions/{id:guid}", async (Guid id, ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            await using var db = await Db(http, ct);
            var session = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == id, ct);
            if (session is null)
            {
                return SessionNotFound(id);
            }

            if (!CanRead(principal, session.Actor))
            {
                return Forbidden();
            }

            return Results.Ok(View(session));
        });

        group.MapGet("/sessions/{id:guid}/messages", async (Guid id, ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            await using var db = await Db(http, ct);
            var owner = await db.ChatSessions.AsNoTracking()
                .Where(s => s.SessionId == id)
                .Select(s => s.Actor)
                .FirstOrDefaultAsync(ct);
            if (owner is null)
            {
                return SessionNotFound(id);
            }

            if (!CanRead(principal, owner))
            {
                return Forbidden();
            }

            var messages = await processor.MessagesAsync(id, ct);
            var actions = await db.ChatActions.AsNoTracking()
                .Where(a => a.SessionId == id)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(messages.Select(m => new ChatMessageView(
                m.MessageId, m.Role, m.Text, m.Basis, m.TemplateId, m.CreatedAt,
                actions.Where(a => a.MessageId == m.MessageId).Select(ActionView).ToArray())).ToArray());
        });

        // Owner-only: a read-all admin may browse, not speak in, another's
        // session — the turn's audit attribution is the speaker's.
        group.MapPost("/sessions/{id:guid}/messages", async (Guid id, ChatMessageRequest request, ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            if (request.Text is not { Length: > 0 } text || text.Length > 32_000)
            {
                return Results.Problem(ProblemErrors.Create(
                    "invalid-chat-message", "Invalid chat message", StatusCodes.Status400BadRequest,
                    "text is required (non-empty, <= 32000 chars)"));
            }

            await using var db = await Db(http, ct);
            var owner = await db.ChatSessions.AsNoTracking()
                .Where(s => s.SessionId == id)
                .Select(s => s.Actor)
                .FirstOrDefaultAsync(ct);
            if (owner is null)
            {
                return SessionNotFound(id);
            }

            if (!string.Equals(owner, principal.Actor, StringComparison.Ordinal))
            {
                return Forbidden();
            }

            return Results.Ok(await processor.PostMessageAsync(id, text, principal, ct));
        });

        group.MapGet("/actions/{id:guid}", async (Guid id, ChatProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is null)
            {
                return Unauthorized();
            }

            var loaded = await processor.LoadActionAsync(id, ct);
            return loaded is null
                ? ActionNotFound(id)
                : Results.Ok(RowView(loaded.Value.Action));
        });

        group.MapPost("/actions/{id:guid}/confirm", async (Guid id, ChatProcessor processor, ChatActionExecutor executor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            var loaded = await processor.LoadActionAsync(id, ct);
            if (loaded is null)
            {
                return ActionNotFound(id);
            }

            if (!CanRead(principal, loaded.Value.Session.Actor))
            {
                return Forbidden();
            }

            var result = await executor.ConfirmAsync(id, principal, http.Request, ct);
            return result.Outcome switch
            {
                ChatActionOutcome.Done => Results.Ok(RowView(result.Action!)),
                ChatActionOutcome.Conflict => ActionConflict(id),
                _ => ActionNotFound(id),
            };
        });

        group.MapPost("/actions/{id:guid}/cancel", async (Guid id, ChatProcessor processor, ChatActionExecutor executor, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            var loaded = await processor.LoadActionAsync(id, ct);
            if (loaded is null)
            {
                return ActionNotFound(id);
            }

            if (!CanRead(principal, loaded.Value.Session.Actor))
            {
                return Forbidden();
            }

            var result = await executor.CancelAsync(id, ct);
            return result.Outcome switch
            {
                ChatActionOutcome.Done => Results.Ok(RowView(result.Action!)),
                ChatActionOutcome.Conflict => ActionConflict(id),
                _ => ActionNotFound(id),
            };
        });

        group.MapGet("/preferences", async (HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            await using var db = await Db(http, ct);
            var row = await db.ActorPreferences.AsNoTracking()
                .Where(p => p.Actor == principal.Actor)
                .Select(p => new { p.Verbosity, p.UpdatedAt })
                .FirstOrDefaultAsync(ct);
            var fallback = AiPolicyStore.Parse(await LoadLlmPayloadAsync(db, ct));
            return Results.Ok(new
            {
                verbosity = row?.Verbosity ?? fallback.VerbosityDefault,
                updatedAt = row?.UpdatedAt,
                source = row is null ? "default" : "actor",
            });
        });

        // Self only: the key IS the principal's canonical actor — a PUT can
        // never address another actor, so no role beyond chat.use is needed.
        group.MapPut("/preferences", async (ChatPreferencesRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (http.GetFacilitatorPrincipal() is not { } principal)
            {
                return Unauthorized();
            }

            if (request.Verbosity is not ("terse" or "balanced" or "proactive"))
            {
                return Results.Problem(ProblemErrors.Create(
                    "invalid-preference", "Invalid preference", StatusCodes.Status400BadRequest,
                    "verbosity must be terse, balanced, or proactive"));
            }

            await using var db = await Db(http, ct);
            var now = DateTime.UtcNow;
            var existing = await db.ActorPreferences.FirstOrDefaultAsync(p => p.Actor == principal.Actor, ct);
            if (existing is null)
            {
                db.ActorPreferences.Add(new ActorPreferenceEntity
                {
                    Actor = principal.Actor,
                    Verbosity = request.Verbosity,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.Verbosity = request.Verbosity;
                existing.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { verbosity = request.Verbosity, updatedAt = now, source = "actor" });
        });

        return app;
    }

    // Ownership law shared by every cross-principal read and by confirm/cancel:
    // own session, or the granted chat.read-all role (the gate populates
    // principal.Roles; the endpoint re-checks the semantic, like all=true).
    private static bool CanRead(FacilitatorPrincipal principal, string sessionActor) =>
        string.Equals(sessionActor, principal.Actor, StringComparison.Ordinal)
        || principal.Roles.Contains(ReadAllRole);

    private static Task<FacilitatorDbContext> Db(HttpContext http, CancellationToken ct) =>
        http.RequestServices.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync(ct);

    private static async Task<string?> LoadLlmPayloadAsync(FacilitatorDbContext db, CancellationToken ct) =>
        await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "llm")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);

    private static ChatSessionView View(ChatSessionEntity s) =>
        new(s.SessionId, s.Actor, s.Title, s.CreatedAt, s.LastMessageAt);

    private static ChatActionView ActionView(ChatActionEntity a) =>
        new(a.ActionId, a.Method, a.Path,
            a.Body is { Length: > 0 } body ? JsonDocument.Parse(body).RootElement.Clone() : null,
            a.Description, a.Status);

    private static ChatActionRowView RowView(ChatActionEntity a) =>
        new(a.ActionId, a.SessionId, a.Method, a.Path,
            a.Body is { Length: > 0 } body ? JsonDocument.Parse(body).RootElement.Clone() : null,
            a.Description, a.Status, a.StatusCode, a.Result, a.ExecutedBy, a.ExecutedAt);

    private static IResult SessionNotFound(Guid id) =>
        Results.Problem(ProblemErrors.Create("chat-session-not-found", "Chat session not found", StatusCodes.Status404NotFound, $"no chat session {id}"));

    private static IResult ActionNotFound(Guid id) =>
        Results.Problem(ProblemErrors.Create("chat-action-not-found", "Chat action not found", StatusCodes.Status404NotFound, $"no chat action {id}"));

    // Status fence lost (already executed/cancelled, or a concurrent claim).
    private static IResult ActionConflict(Guid id) =>
        Results.Problem(ProblemErrors.Create("chat-action-not-pending", "Chat action not pending", StatusCodes.Status409Conflict,
            $"chat action {id} is no longer pending or was concurrently claimed"));

    private static IResult Unauthorized() => Results.Problem(IdentityAuthentication.Unauthorized());

    private static IResult Forbidden() =>
        Results.Problem(IdentityAuthentication.Forbidden("chat.use", ["chat.use", "chat.execute", ReadAllRole]));
}
