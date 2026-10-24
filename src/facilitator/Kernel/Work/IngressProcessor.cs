using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Dispatch;
using Facilitator.Kernel.Domain;
using Facilitator.Kernel.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Ingress;

/// <summary>Wire DTO for a Work (camelCase via serializer options).</summary>
/// <param name="WorkId">Work identity.</param>
/// <param name="ProjectId">Owning project when bound.</param>
/// <param name="Objective">Objective text.</param>
/// <param name="State">Wire state name.</param>
/// <param name="Version">Current fencing version.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last mutation time (UTC).</param>
public sealed record WorkDto(Guid WorkId, Guid? ProjectId, string Objective, string State, long Version, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Write-path seam for the ingress endpoints: inbox dedupe insert (ON CONFLICT
/// DO NOTHING, spec §7.1) and the raw-SQL queries the endpoints expose.
/// </summary>
public sealed class IngressStore(IDbContextFactory<FacilitatorDbContext> contextFactory, TimeProvider timeProvider)
{
    private const string InsertInboxSql = """
        INSERT INTO facilitator.inbox AS i
            (inbox_id, event_source, event_id, command_id, provider, provider_delivery_id,
             payload, payload_sha256, work_id, state, available_at, attempt, received_at, processed_at)
        VALUES
            (@id, @source, @eventId, @commandId, NULL, NULL,
             @payload::jsonb, @sha, @workId, @state, @availableAt, @attempt, @receivedAt, @processedAt)
        ON CONFLICT DO NOTHING
        RETURNING i.inbox_id
        """;

    /// <summary>
    /// Insert an inbox row unless the (event_source,event_id) or command_id
    /// unique already exists. Returns (inboxId, duplicate).
    /// </summary>
    public async Task<(Guid InboxId, bool Duplicate)> InsertInboxAsync(
        Guid inboxId,
        string eventSource,
        string eventId,
        string? commandId,
        string payloadJson,
        Guid? workId,
        string state,
        DateTime? processedAt,
        CancellationToken ct)
    {
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = InsertInboxSql;
        Add(command, "@id", inboxId);
        Add(command, "@source", eventSource);
        Add(command, "@eventId", eventId);
        Add(command, "@commandId", (object?)commandId ?? DBNull.Value);
        Add(command, "@payload", payloadJson);
        Add(command, "@sha", sha);
        Add(command, "@workId", (object?)workId ?? DBNull.Value);
        Add(command, "@state", state);
        Add(command, "@availableAt", now);
        Add(command, "@attempt", processedAt is null ? 0 : 1);
        Add(command, "@receivedAt", now);
        Add(command, "@processedAt", (object?)processedAt ?? DBNull.Value);
        var connection = command.Connection ?? throw new InvalidOperationException("command has no connection");
        await connection.OpenAsync(ct);
        var inserted = await command.ExecuteScalarAsync(ct);
        if (inserted is Guid insertedId)
        {
            return (insertedId, false);
        }

        await using var probe = await contextFactory.CreateDbContextAsync(ct);
        var existing = await probe.Inbox.AsNoTracking()
            .Where(i => (i.EventSource == eventSource && i.EventId == eventId)
                     || (commandId != null && i.CommandId == commandId))
            .Select(i => new { i.InboxId })
            .FirstOrDefaultAsync(ct);
        return (existing?.InboxId ?? inboxId, true);
    }

    /// <summary>
    /// Settle an inbox row: <c>processed</c> on success, <c>failed</c> when an
    /// error is recorded — dedupe-on-success (appendix §5.3).
    /// </summary>
    public async Task MarkProcessedAsync(Guid inboxId, string? error, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.inbox
                SET state = {(error is null ? "processed" : "failed")}, processed_at = {now}, error = {error}
              WHERE inbox_id = {inboxId}
             """, ct);
    }

    /// <summary>Append a timeline row without touching the version fence (audit notes).</summary>
    public async Task AppendTimelineNoteAsync(Guid workId, string kind, string actor, string detailJson, string? correlationId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var version = await db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = kind,
            Actor = actor,
            Detail = detailJson,
            CorrelationId = correlationId,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Newest 50 works, newest first.</summary>
    public async Task<IReadOnlyList<WorkView>> ListRecentAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Works.AsNoTracking()
            .OrderByDescending(w => w.CreatedAt)
            .Take(50)
            .Select(w => new WorkView(w.WorkId, w.ProjectId, w.Objective, w.State, w.Version, w.CreatedAt, w.UpdatedAt))
            .ToListAsync(ct);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// Inline ingress processing (appendix §5.3, spec §19.3): validate → inbox
/// dedupe insert → business effect through the SAME coordination-store
/// transactions a future poller would use → NOTIFY. Slice 1 is
/// synchronous-by-design; the iteration happens per request.
/// </summary>
public sealed class IngressProcessor(
    ICoordinationStore store,
    IngressStore inbox,
    EnvelopeValidator validator,
    KernelMetrics metrics,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Wire limit every ingress body is checked against.</summary>
    public const int MaxBodyBytes = 262144;

    /// <summary>Create a Work (synthetic inbox row participates in dedupe).</summary>
    public async Task<IResult> CreateWorkAsync(JsonElement body, CancellationToken ct)
    {
        var objective = body.TryGetProperty("objective", out var o) ? o.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return Problem(ProblemErrors.SchemaViolation("objective is required"));
        }

        Guid? projectId = null;
        if (body.TryGetProperty("projectId", out var pid) && pid.ValueKind == JsonValueKind.String && Guid.TryParse(pid.GetString(), out var parsed))
        {
            projectId = parsed;
        }

        var commandId = body.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        commandId ??= Guid.NewGuid().ToString("N");

        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), "urn:facilitator:http", Guid.NewGuid().ToString("N"), commandId,
            body.GetRawText(), null, "pending", null, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        var work = await WithSpanAsync("ingress.process", async () =>
            await store.CreateWorkAsync(objective, projectId, "http-operator", ct));
        await inbox.MarkProcessedAsync(inboxId, null, ct);
        metrics.InboxProcessed();
        return Results.Created($"/api/works/{work.WorkId}", ToDto(work));
    }

    /// <summary>Guard-checked transition.</summary>
    public async Task<IResult> TransitionAsync(Guid workId, JsonElement body, CancellationToken ct)
    {
        var toWire = body.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (toWire is null || !StateGuard.TryParseWork(toWire, out var to))
        {
            return Problem(ProblemErrors.SchemaViolation($"'{toWire}' is not a Work state"));
        }

        var expected = body.TryGetProperty("expectedVersion", out var v) && v.TryGetInt64(out var parsed) ? parsed : -1;
        if (expected < 0)
        {
            return Problem(ProblemErrors.SchemaViolation("expectedVersion is required"));
        }

        var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var actor = "http-operator";

        var result = await WithSpanAsync("ingress.process", async () =>
            await store.ApplyTransitionAsync(workId, to, expected, actor, reason, ct));
        return result switch
        {
            TransitionResult.Ok ok => Results.Ok(ToDto(ok.Work)),
            TransitionResult.Conflict conflict => Problem(ProblemErrors.VersionConflict(conflict.CurrentVersion)),
            TransitionResult.Illegal illegal => Problem(ProblemErrors.InvalidTransition(illegal.Edge)),
            _ => Problem(ProblemErrors.WorkNotFound(workId)),
        };
    }

    /// <summary>Steer a work (version-fenced; commandId participates in dedupe).</summary>
    public async Task<IResult> SteerAsync(Guid workId, JsonElement body, CancellationToken ct)
    {
        var directive = body.TryGetProperty("directive", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var commandId = body.TryGetProperty("commandId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var expected = body.TryGetProperty("expectedVersion", out var v) && v.TryGetInt64(out var parsed) ? parsed : -1;
        if (directive is null || commandId is null || expected < 0)
        {
            return Problem(ProblemErrors.SchemaViolation("directive, commandId and expectedVersion are required"));
        }

        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), "urn:facilitator:http", Guid.NewGuid().ToString("N"), commandId,
            body.GetRawText(), workId, "pending", null, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        var result = await WithSpanAsync("ingress.process", async () =>
            await store.ApplySteerAsync(workId, directive, commandId, expected, ct));
        return result switch
        {
            SteerResult.Ok ok => await SettleAsync(inboxId, null, ct, () => Results.Ok(ToDto(ok.Work))),
            SteerResult.Conflict conflict => await SettleAsync(
                inboxId, "version-conflict", ct, () => Problem(ProblemErrors.VersionConflict(conflict.CurrentVersion))),
            SteerResult.Terminal => await SettleAsync(
                inboxId, "terminal-state", ct, () => Problem(ProblemErrors.InvalidTransition("terminal→steer"))),
            SteerResult.Duplicate => await SettleAsync(
                inboxId, null, ct, () => Results.Ok(new { status = "duplicate", inboxId })),
            _ => await SettleAsync(
                inboxId, "work-not-found", ct, () => Problem(ProblemErrors.WorkNotFound(workId))),
        };
    }

    /// <summary>Settle the inbox row (processed/failed) and produce the response.</summary>
    private async Task<IResult> SettleAsync(Guid inboxId, string? error, CancellationToken ct, Func<IResult> response)
    {
        await inbox.MarkProcessedAsync(inboxId, error, ct);
        metrics.InboxProcessed();
        return response();
    }

    /// <summary>
    /// Full CloudEvent ingress pipeline: size gate → envelope validation →
    /// fail-closed unknown-command (with a failed audit inbox row) → dedupe
    /// insert → inline business effect for known commands → 202 for events.
    /// </summary>
    public async Task<IResult> IngestEventAsync(string rawBody, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(rawBody);
        if (bytes > MaxBodyBytes)
        {
            return Problem(ProblemErrors.PayloadTooLarge(bytes));
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
        }

        var verdict = validator.Validate(root, bytes);
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
        if (!verdict.Ok)
        {
            if (verdict.ErrorClass == "unknown-command")
            {
                // §5.4: fail closed, but persist the audit row.
                await inbox.InsertInboxAsync(
                    Guid.NewGuid(), SourceOf(root), EventIdOf(root), null, rawBody,
                    null, "failed", null, ct);
                return Problem(ProblemErrors.UnknownCommand(type));
            }

            return verdict.ErrorClass switch
            {
                "payload-too-large" => Problem(ProblemErrors.PayloadTooLarge(bytes)),
                "schema-violation" => Problem(ProblemErrors.SchemaViolation(verdict.Message ?? "schema violation")),
                _ => Problem(ProblemErrors.EnvelopeViolation(verdict.Message ?? "envelope violation")),
            };
        }

        var isCommand = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal);
        var commandId = isCommand && root.GetProperty("data").TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        var state = isCommand ? "pending" : "processed";
        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), SourceOf(root), EventIdOf(root), commandId, rawBody,
            null, state, isCommand ? null : timeProvider.GetUtcNow().UtcDateTime, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        if (!isCommand)
        {
            metrics.InboxProcessed();
            return Results.Accepted(null, new { status = "accepted", inboxId });
        }

        var workId = WorkIdOf(root);
        IResult result;
        if (type == "org.facilitator.command.work.steer.v1" && workId is not null)
        {
            var data = root.GetProperty("data");
            var directive = data.GetProperty("directive").GetString() ?? string.Empty;
            var expectedVersion = data.TryGetProperty("expectedContextVersion", out var ev) && ev.TryGetInt64(out var parsed) ? parsed : 0;
            var steer = await WithSpanAsync("ingress.process", async () =>
                await store.ApplySteerAsync(workId.Value, directive, commandId!, expectedVersion, ct));
            result = steer switch
            {
                SteerResult.Ok ok => await SettleAsync(
                    inboxId, null, ct, () => Results.Ok(new { status = "accepted", inboxId, work = ToDto(ok.Work) })),
                SteerResult.Conflict conflict => await SettleAsync(
                    inboxId, "version-conflict", ct, () => Problem(ProblemErrors.VersionConflict(conflict.CurrentVersion))),
                SteerResult.Terminal => await SettleAsync(
                    inboxId, "terminal-state", ct, () => Problem(ProblemErrors.InvalidTransition("terminal→steer"))),
                SteerResult.Duplicate => await SettleAsync(
                    inboxId, null, ct, () => Results.Ok(new { status = "duplicate", inboxId })),
                _ => await SettleAsync(
                    inboxId, "work-not-found", ct, () => Problem(ProblemErrors.WorkNotFound(workId.Value))),
            };
        }
        else
        {
            // Known command with no Plan 02 business effect: processed + a
            // typed timeline note (not silent), when the work is known.
            if (workId is not null)
            {
                await inbox.AppendTimelineNoteAsync(workId.Value, "command.accepted", "cloud-event",
                    JsonSerializer.Serialize(new Dictionary<string, string?> { ["type"] = type, ["commandId"] = commandId }, Json),
                    commandId, ct);
            }

            result = await SettleAsync(
                inboxId, null, ct, () => Results.Accepted(null, new { status = "accepted", inboxId }));
        }

        return result;
    }

    /// <summary>Map a WorkView to its wire DTO.</summary>
    public static WorkDto ToDto(WorkView work) =>
        new(work.WorkId, work.ProjectId, work.Objective, work.State, work.Version, work.CreatedAt, work.UpdatedAt);

    private static string SourceOf(JsonElement root) =>
        root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "urn:facilitator:unknown";

    private static string EventIdOf(JsonElement root) =>
        root.TryGetProperty("id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : Guid.NewGuid().ToString("N");

    private static Guid? WorkIdOf(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.TryGetProperty("workId", out var wid)
            && wid.ValueKind == JsonValueKind.String
            && Guid.TryParse(wid.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private async Task<T> WithSpanAsync<T>(string name, Func<Task<T>> action)
    {
        using var span = KernelActivity.Source.StartActivity(name, ActivityKind.Internal);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            metrics.IngressLatency(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static IResult Problem(ProblemDetails problem) => Results.Problem(problem);
}
