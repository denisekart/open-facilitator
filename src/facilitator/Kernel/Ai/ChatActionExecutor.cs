using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>Confirm/cancel outcome codes mapped to HTTP by the endpoints.</summary>
public enum ChatActionOutcome
{
    /// <summary>Executed (executed or failed by response status — the response carries the row).</summary>
    Done,

    /// <summary>No such action row.</summary>
    NotFound,

    /// <summary>Row belongs to another owner and the principal lacks chat.read-all.</summary>
    Forbidden,

    /// <summary>Status fence lost: not pending anymore, or concurrent claim (single execution law).</summary>
    Conflict,
}

/// <summary>Executor result: outcome + the terminal row when Done.</summary>
/// <param name="Outcome">Result code.</param>
/// <param name="Action">Terminal row (Done only).</param>
public sealed record ChatActionResult(ChatActionOutcome Outcome, ChatActionEntity? Action);

/// <summary>
/// Executes a confirmed chat action. The claim is a status-guard UPDATE
/// <c>pending → executed</c> fenced on <c>status='pending' AND
/// row_version</c> via <c>ExecuteUpdateAsync</c> — exactly one confirmer wins
/// (0 rows ⇒ 409); the row stays terminal through the whole call because the
/// status check constraint has no in-flight state. THEN, outside any
/// transaction, the stored method/path/body are replayed as loopback HTTP on
/// the kernel itself with the confirming admin's <c>Authorization</c> header
/// forwarded verbatim — so the role gate, RBAC bindings, and the audit writer
/// fire exactly as if the admin pressed the button in the admin UI. The
/// response status decides executed vs failed (the response is recorded
/// either way: the gate decided, the record shows it). Base URL:
/// <c>Ai:SelfBaseUrl</c> config when set, else the confirming request's own
/// scheme+host.
/// </summary>
public sealed class ChatActionExecutor(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    AuditWriter audit,
    KernelMetrics metrics,
    TimeProvider timeProvider)
{
    /// <summary>Stored response cap (8 KiB).</summary>
    public const int ResultLimitBytes = 8192;

    /// <summary>Claim + replay + finalize one action.</summary>
    /// <param name="actionId">Action row.</param>
    /// <param name="principal">Confirming admin (owner or chat.read-all — checked by the caller).</summary>
    /// <param name="request">Confirming request (Authorization + X-Change-Reason forwarded; scheme+host fallback base).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ChatActionResult> ConfirmAsync(Guid actionId, FacilitatorPrincipal principal, HttpRequest request, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.ChatActions.AsNoTracking()
            .Where(a => a.ActionId == actionId)
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return new ChatActionResult(ChatActionOutcome.NotFound, null);
        }

        var loadedVersion = row.RowVersion;

        // Single-execution fence: claim pending -> executed BEFORE the HTTP
        // (constraint has no in-flight state; a crash mid-call leaves an
        // executed row whose recorded absence the audit still explains).
        var claimed = await db.ChatActions
            .Where(a => a.ActionId == actionId && a.Status == "pending" && a.RowVersion == loadedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, "executed")
                .SetProperty(a => a.RowVersion, a => a.RowVersion + 1), ct);
        if (claimed == 0)
        {
            return new ChatActionResult(ChatActionOutcome.Conflict, null);
        }

        var target = BaseUrl(request);
        using var span = KernelActivity.StartAiActionExecuteSpan("loopback");
        var (status, responseBody) = await ReplayAsync(row, target, request, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var finalStatus = status >= 200 && status < 300 ? "executed" : "failed";

        await db.ChatActions
            .Where(a => a.ActionId == actionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, finalStatus)
                .SetProperty(a => a.StatusCode, status)
                .SetProperty(a => a.Result, responseBody)
                .SetProperty(a => a.ExecutedBy, principal.Actor)
                .SetProperty(a => a.ExecutedAt, now), ct);

        // The gate on the replayed call audited its own decision; this row
        // attributes the AI-initiated execution to the confirming admin.
        audit.Append(db, new AuditRecord(
            principal.Actor, "chat.action.executed", $"{row.Method} {row.Path}", "allow", null, null, null,
            JsonSerializer.Serialize(new { aiInitiated = true, chatSessionId = row.SessionId, actionId })));
        await db.SaveChangesAsync(ct);

        metrics.AiChatAction(finalStatus);
        var final = await db.ChatActions.AsNoTracking().FirstAsync(a => a.ActionId == actionId, ct);
        return new ChatActionResult(ChatActionOutcome.Done, final);
    }

    /// <summary>Status-guard <c>pending → cancelled</c>; a non-pending row is 409.</summary>
    public async Task<ChatActionResult> CancelAsync(Guid actionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var exists = await db.ChatActions.AnyAsync(a => a.ActionId == actionId, ct);
        if (!exists)
        {
            return new ChatActionResult(ChatActionOutcome.NotFound, null);
        }

        var claimed = await db.ChatActions
            .Where(a => a.ActionId == actionId && a.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, "cancelled")
                .SetProperty(a => a.ExecutedAt, timeProvider.GetUtcNow().UtcDateTime), ct);
        if (claimed == 0)
        {
            return new ChatActionResult(ChatActionOutcome.Conflict, null);
        }

        metrics.AiChatAction("cancelled");
        return new ChatActionResult(ChatActionOutcome.Done, await db.ChatActions.AsNoTracking().FirstAsync(a => a.ActionId == actionId, ct));
    }

    // Loopback replay: named client "chat-loopback" (plain IHttpClientFactory
    // client — no per-name registration needed beyond the factory itself).
    private async Task<(int Status, string? Result)> ReplayAsync(ChatActionEntity row, Uri baseUri, HttpRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(row.Method), new Uri(baseUri, row.Path));
        var authorization = request.Headers.TryGetValue("Authorization", out var authValues) ? authValues.ToString() : null;
        if (authorization is { Length: > 0 })
        {
            message.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var changeReason = request.Headers.TryGetValue("X-Change-Reason", out var reasonValues) ? reasonValues.ToString() : null;
        if (changeReason is { Length: > 0 })
        {
            message.Headers.TryAddWithoutValidation("X-Change-Reason", changeReason);
        }

        if (row.Body is { Length: > 0 } body)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await httpClientFactory.CreateClient("chat-loopback").SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return ((int)response.StatusCode, TruncateUtf8(text, ResultLimitBytes));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The replay itself never reached a verdict — record it failed.
            return (StatusCodes.Status502BadGateway, JsonSerializer.Serialize(new { error = "chat-loopback-unreachable", detail = e.Message }));
        }
    }

    // Ai:SelfBaseUrl is a plain config key (behind TLS termination the request's
    // own Host header may not be the reachable base).
    private Uri BaseUrl(HttpRequest request)
    {
        var configured = configuration["Ai:SelfBaseUrl"];
        if (configured is { Length: > 0 } self && Uri.TryCreate(self.TrimEnd('/') + "/", UriKind.Absolute, out var configuredUri))
        {
            return configuredUri;
        }
        var scheme = request.Scheme;
        var host = request.Host.HasValue ? request.Host.ToString() : "localhost";
        return Uri.TryCreate($"{scheme}://{host}/", UriKind.Absolute, out var built)
            ? built
            : new Uri("http://localhost/");
    }

    private static string TruncateUtf8(string value, int limitBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= limitBytes)
        {
            return value;
        }

        // Trim by char until the UTF-8 length fits (no split surrogate pairs).
        var length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > limitBytes)
        {
            length = Math.Max(0, length - Math.Max(1, Encoding.UTF8.GetByteCount(value.AsSpan(length - 1, 1))));
        }

        return value[..length];
    }
}
