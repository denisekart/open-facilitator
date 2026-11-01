using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The full CloudEvent ingress pipeline behind <c>POST /api/ingress/events</c>:
/// size gate → envelope validation → fail-closed unknown-command (with a
/// failed audit inbox row) → dedupe insert → inline business effect for known
/// commands → 202 for events.
/// </summary>
public sealed class CloudEventIngress(
    ICoordinationStore store,
    IngressStore inbox,
    IngressPipeline pipeline,
    EnvelopeValidator validator,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    ApprovalIngress approvals,
    ArtifactIngress artifacts,
    ResultIngress results,
    MemoryIngress memory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validate, dedupe, and inline-process one raw CloudEvent body.</summary>
    public async Task<IResult> IngestEventAsync(string rawBody, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(rawBody);
        if (bytes > RequestBodies.MaxBodyBytes)
        {
            return IngressPipeline.Problem(ProblemErrors.PayloadTooLarge(bytes));
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return IngressPipeline.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
        }

        var verdict = validator.Validate(root, bytes);
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
        if (!verdict.Ok)
        {
            if (verdict.ErrorClass == "unknown-command")
            {
                // §5.4: fail closed, but persist the audit row.
                await inbox.InsertInboxAsync(
                    Guid.NewGuid(), EnvelopeReader.SourceOf(root), EnvelopeReader.EventIdOf(root), null, rawBody,
                    null, "failed", null, ct);
                return IngressPipeline.Problem(ProblemErrors.UnknownCommand(type));
            }

            return verdict.ErrorClass switch
            {
                "payload-too-large" => IngressPipeline.Problem(ProblemErrors.PayloadTooLarge(bytes)),
                "schema-violation" => IngressPipeline.Problem(ProblemErrors.SchemaViolation(verdict.Message ?? "schema violation")),
                _ => IngressPipeline.Problem(ProblemErrors.EnvelopeViolation(verdict.Message ?? "envelope violation")),
            };
        }

        var isCommand = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal);
        var commandId = isCommand && root.GetProperty("data").TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        var state = isCommand ? "pending" : "processed";
        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), EnvelopeReader.SourceOf(root), EnvelopeReader.EventIdOf(root), commandId, rawBody,
            null, state, isCommand ? null : timeProvider.GetUtcNow().UtcDateTime, ct);
        if (duplicate)
        {
            return Results.Ok(new { status = "duplicate", inboxId });
        }

        if (!isCommand)
        {
            if (type is "org.facilitator.event.approval.required.v1" or "org.facilitator.event.approval.revoked.v1")
            {
                var arm = await approvals.HandleEventAsync(root, type, null, ct);
                if (arm.Refused)
                {
                    return await pipeline.SettleAsync(
                        inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
                }

                metrics.InboxProcessed();
                return Results.Accepted(null, new { status = arm.Status, approvalId = arm.ApprovalId, inboxId });
            }

            if (type == "org.facilitator.event.artifact.available.v1")
            {
                var arm = await artifacts.HandleEventAsync(root, ct);
                if (arm.Refused)
                {
                    return await pipeline.SettleAsync(
                        inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
                }

                metrics.InboxProcessed();
                return Results.Accepted(null, new { status = arm.Status, inboxId });
            }

            if (type == ResultIngress.CompletedType || type == ResultIngress.FailedType)
            {
                var arm = type == ResultIngress.CompletedType
                    ? await results.HandleCompletedAsync(root, null, ct)
                    : await results.HandleFailedAsync(root, ct);
                if (arm.Refused)
                {
                    return await pipeline.SettleAsync(
                        inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
                }

                metrics.InboxProcessed();
                return Results.Accepted(null, new { status = arm.Status, inboxId });
            }

            if (type == MemoryIngress.CandidateType || type == MemoryIngress.UsedType)
            {
                // Plan 16: the frozen memory events on the CE plane run the
                // same arms; no AdmissionGate exists here, so the command's
                // actorEvidenceRef law does not apply to events (they never
                // carried one) — the workload context is absent, the broker
                // records what the event's own provenance claims.
                var arm = type == MemoryIngress.CandidateType
                    ? await memory.HandleCandidateAsync(root, null, ct)
                    : await memory.HandleUsedAsync(root, null, ct);
                if (arm.Refused)
                {
                    return await pipeline.SettleAsync(
                        inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
                }

                metrics.InboxProcessed();
                return Results.Accepted(null, new { status = arm.Status, inboxId });
            }

            metrics.InboxProcessed();
            return Results.Accepted(null, new { status = "accepted", inboxId });
        }

        var workId = EnvelopeReader.WorkIdOf(root);
        if (type == "org.facilitator.command.work.steer.v1" && workId is not null)
        {
            var data = root.GetProperty("data");
            var directive = data.GetProperty("directive").GetString() ?? string.Empty;
            var expectedVersion = data.TryGetProperty("expectedContextVersion", out var ev) && ev.TryGetInt64(out var parsed) ? parsed : 0;
            var steer = await pipeline.WithSpanAsync("ingress.process", async () =>
                await store.ApplySteerAsync(workId.Value, directive, commandId!, expectedVersion, ct));
            return await pipeline.HandleSteerResultAsync(
                steer, inboxId, workId.Value,
                work => Results.Ok(new { status = "accepted", inboxId, work = WorkDtoMapper.ToDto(work) }),
                () => Results.Ok(new { status = "duplicate", inboxId }),
                ct);
        }

        if (type == "org.facilitator.command.approval.respond.v1")
        {
            // No AdmissionGate on this surface: the human identity rides the
            // command's actorEvidenceRef, evaluated inline (law D4).
            var (actor, groups) = await approvals.ResolveWireActorAsync(root, ct);
            var arm = await approvals.HandleRespondAsync(root, actor, groups, null, mintEvidence: false, ct);
            if (arm.Refused)
            {
                return await pipeline.SettleAsync(
                    inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
            }

            return await pipeline.SettleAsync(
                inboxId, null, ct,
                () => Results.Ok(new { status = arm.Status, approvalId = arm.ApprovalId, inboxId }));
        }

        if (type == MemoryIngress.RetainType || type == MemoryIngress.RecallType)
        {
            // No AdmissionGate on this surface: the command's actorEvidenceRef
            // must validate inline (the approvals law); the receipt/response
            // rides the 202 body's data field (the link-plane precedent).
            var (actor, groups) = await approvals.ResolveWireActorAsync(root, ct);
            var linkPrincipal = WirePrincipal(actor, groups);
            var arm = type == MemoryIngress.RetainType
                ? await memory.HandleRetainAsync(root, linkPrincipal, ct)
                : await memory.HandleRecallAsync(root, linkPrincipal, ct);
            if (arm.Refused)
            {
                return await pipeline.SettleAsync(
                    inboxId, arm.Status, ct, () => IngressPipeline.Problem(arm.Problem!));
            }

            return await pipeline.SettleAsync(
                inboxId, null, ct,
                () => Results.Accepted(null, new { status = arm.Status, inboxId, data = arm.Data }));
        }

        // Known command with no Plan 02 business effect: processed + a
        // typed timeline note (not silent), when the work is known.
        if (workId is not null)
        {
            await inbox.AppendTimelineNoteAsync(workId.Value, "command.accepted", "cloud-event",
                JsonSerializer.Serialize(new Dictionary<string, string?> { ["type"] = type, ["commandId"] = commandId }, Json),
                commandId, ct);
        }

        return await pipeline.SettleAsync(
            inboxId, null, ct, () => Results.Accepted(null, new { status = "accepted", inboxId }));
    }

    /// <summary>
    /// A link-plane context synthesized from a validated wire actor on the
    /// ungated CE surface: the endpoint identity is the ingress itself, the
    /// team is the actor's evidence groups' team (memory's own
    /// team-from-groups law). No team ⇒ the broker's own denials apply.
    /// </summary>
    private static EffectivePrincipal? WirePrincipal(string? actor, IReadOnlyList<string> groups)
    {
        var team = Facilitator.Kernel.Shared.Storage.ArtifactLedger.TeamOfGroups(groups);
        if (team is null)
        {
            return null;
        }

        var workload = new Facilitator.Kernel.Shared.Auth.LinkPrincipal(
            "ingress", Guid.Empty, team, "evidence", "workload");
        var delegated = actor is null
            ? null
            : new Facilitator.Kernel.Shared.Evidence.DelegatedIdentity(actor, groups);
        return new EffectivePrincipal(workload, delegated, actor is null ? "workload-only" : "delegation-validated");
    }
}
