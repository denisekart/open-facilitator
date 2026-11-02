using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// Inline ingress use-cases: every write is
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

        // Routing intents: an explicit `assignments[]` array, or the
        // implicit single assignment from the top-level capability pair.
        // The funnel resolves the endpoint; a create without any capability
        // stays a bare Work (operator dispatches later).
        List<RoutingIntentSeed>? seeds = null;
        if (body.TryGetProperty("assignments", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            seeds = [];
            foreach (var item in arr.EnumerateArray())
            {
                var cap = item.TryGetProperty("capabilityId", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(cap))
                {
                    return IngressPipeline.Problem(ProblemErrors.SchemaViolation("assignments[].capabilityId is required"));
                }

                seeds.Add(new RoutingIntentSeed(
                    cap,
                    Objective: Str(item, "objective"),
                    TargetId: Str(item, "targetId"),
                    PreferenceEndpointId: Str(item, "preferenceEndpointId"),
                    ActorEvidenceRef: Str(item, "actorEvidenceRef"),
                    Inputs: item.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp.Clone() : null,
                    DependsOn: item.TryGetProperty("dependsOn", out var dep) && dep.ValueKind == JsonValueKind.Array ? dep.Clone() : null));
            }
        }
        else if (body.TryGetProperty("capabilityId", out var topCap) && topCap.ValueKind == JsonValueKind.String && topCap.GetString() is { Length: > 0 } topCapability)
        {
            seeds =
            [
                new RoutingIntentSeed(
                    topCapability,
                    Objective: objective,
                    TargetId: Str(body, "targetId"),
                    PreferenceEndpointId: Str(body, "preferenceEndpointId"),
                    ActorEvidenceRef: Str(body, "actorEvidenceRef")),
            ];
        }

        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), "urn:facilitator:http", Guid.NewGuid().ToString("N"), commandId,
            body.GetRawText(), null, "pending", null, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        WorkView work;
        try
        {
            work = await pipeline.WithSpanAsync("ingress.process", async () =>
                await store.CreateWorkAsync(objective, projectId, actor, ct, seeds));
        }
        catch (DagViolationException dag)
        {
            // Create-time DAG law: the whole request is rejected
            // — the store's tx rolled back, nothing was created.
            await inbox.MarkProcessedAsync(inboxId, "failed", ct);
            return IngressPipeline.Problem(ProblemErrors.DagViolation(dag.Message));
        }
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

    /// <summary>Operator re-dispatch: a fresh routing intent for an existing
    /// Work (the retry / restart-with-context leg). Body:
    /// <c>{capabilityId, objective?, targetId?, preferenceEndpointId?,
    /// actorEvidenceRef?, inputs?, commandId?}</c>; commandId keys the
    /// effect-row ledger for replay idempotency.</summary>
    public async Task<IResult> RedispatchAsync(Guid workId, JsonElement body, CancellationToken ct)
    {
        var capability = body.TryGetProperty("capabilityId", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(capability))
        {
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation("capabilityId is required"));
        }

        var commandId = body.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        var seed = new RoutingIntentSeed(
            capability,
            Objective: Str(body, "objective"),
            TargetId: Str(body, "targetId"),
            PreferenceEndpointId: Str(body, "preferenceEndpointId"),
            ActorEvidenceRef: Str(body, "actorEvidenceRef"),
            Inputs: body.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp.Clone() : null);

        var ok = await pipeline.WithSpanAsync("ingress.process", async () =>
            await store.RedispatchAsync(workId, seed, commandId, ct));
        return ok
            ? Results.Accepted($"/api/works/{workId}", new { status = "redispatch-requested" })
            : IngressPipeline.Problem(ProblemErrors.WorkNotFound(workId));
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
