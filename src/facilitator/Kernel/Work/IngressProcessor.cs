using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// Inline ingress use-cases (appendix §5.3, spec §19.3): every write is
/// inbox-deduped, then runs its business effect through the SAME
/// coordination-store transactions a future poller would use. Slice 1 is
/// synchronous-by-design; the iteration happens per request.
/// </summary>
public sealed class IngressProcessor(
    ICoordinationStore store,
    IngressStore inbox,
    IngressPipeline pipeline,
    KernelMetrics metrics)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Create a Work (synthetic inbox row participates in dedupe).</summary>
    /// <param name="body">Request body.</param>
    /// <param name="actor">Canonical actor of the requesting principal.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IResult> CreateWorkAsync(JsonElement body, string actor, CancellationToken ct)
    {
        var objective = body.TryGetProperty("objective", out var o) ? o.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation("objective is required"));
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

        var work = await pipeline.WithSpanAsync("ingress.process", async () =>
            await store.CreateWorkAsync(objective, projectId, actor, ct));
        await inbox.MarkProcessedAsync(inboxId, null, ct);
        metrics.InboxProcessed();
        return Results.Created($"/api/works/{work.WorkId}", WorkDtoMapper.ToDto(work));
    }

    /// <summary>Guard-checked transition.</summary>
    public async Task<IResult> TransitionAsync(Guid workId, JsonElement body, string actor, CancellationToken ct)
    {
        var toWire = body.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (toWire is null || !StateGuard.TryParseWork(toWire, out var to))
        {
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation($"'{toWire}' is not a Work state"));
        }

        var expected = body.TryGetProperty("expectedVersion", out var v) && v.TryGetInt64(out var parsed) ? parsed : -1;
        if (expected < 0)
        {
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation("expectedVersion is required"));
        }

        var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

        var result = await pipeline.WithSpanAsync("ingress.process", async () =>
            await store.ApplyTransitionAsync(workId, to, expected, actor, reason, ct));
        return result switch
        {
            TransitionResult.Ok ok => Results.Ok(WorkDtoMapper.ToDto(ok.Work)),
            TransitionResult.Conflict conflict => IngressPipeline.Problem(ProblemErrors.VersionConflict(conflict.CurrentVersion)),
            TransitionResult.Illegal illegal => IngressPipeline.Problem(ProblemErrors.InvalidTransition(illegal.Edge)),
            _ => IngressPipeline.Problem(ProblemErrors.WorkNotFound(workId)),
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
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation("directive, commandId and expectedVersion are required"));
        }

        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), "urn:facilitator:http", Guid.NewGuid().ToString("N"), commandId,
            body.GetRawText(), workId, "pending", null, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        var result = await pipeline.WithSpanAsync("ingress.process", async () =>
            await store.ApplySteerAsync(workId, directive, commandId, expected, ct));
        return await pipeline.HandleSteerResultAsync(
            result, inboxId, workId,
            work => Results.Ok(WorkDtoMapper.ToDto(work)),
            () => Results.Ok(new { status = "duplicate", inboxId }),
            ct);
    }
}
