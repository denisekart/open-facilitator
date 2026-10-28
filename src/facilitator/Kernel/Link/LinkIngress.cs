using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Link;

/// <summary>Admission verdict shared by the hub Envelope method and POST /events.</summary>
/// <param name="Problem">ProblemDetails to surface, when the envelope was refused.</param>
/// <param name="Status">Admission status wire value ("accepted"|"duplicate") when admitted.</param>
/// <param name="InboxId">Inbox row identity (receipt deliveryId on the HTTP path).</param>
public sealed record LinkAdmission(ProblemDetails? Problem, string? Status, Guid? InboxId, object? Data = null)
{
    /// <summary>HTTP result equivalent of this verdict (OpenAPI: 202 + receipt body, else typed problem).
    /// Admissions that carry in-band payload (e.g. an upload ticket) surface it as the optional
    /// <c>data</c> field of the success bodies.</summary>
    public IResult ToResult(DateTime processedAt) =>
        Problem is not null ? Results.Problem(Problem)
        : Status == "duplicate"
            ? (Data is null ? Results.Ok(new { status = "duplicate", inboxId = InboxId })
                : Results.Ok(new { status = "duplicate", inboxId = InboxId, data = Data }))
        : (Data is null
            ? Results.Accepted(null, new { deliveryId = InboxId!.Value.ToString("N"), status = Status, processedAt })
            : Results.Accepted(null, new { deliveryId = InboxId!.Value.ToString("N"), status = Status, processedAt, data = Data }));
}

/// <summary>
/// ONE admission code path for link-originated envelopes, shared by the
/// SignalR <c>Envelope</c> hub method and <c>POST /api/link/v1/events</c>
/// (identical semantics on both transports). The validation prefix lives in
/// <see cref="AdmissionGate"/>; this class keeps dedupe (the shared
/// <see cref="IngressStore"/> insert, spec S:301) + dispatch: work.steer runs
/// the SAME ApplySteerAsync path as Work's ingress, the other known commands
/// settle as a processed no-op, event-class settles accepted with no side
/// effects, and the receipt-class / assign / unknown-command classes were
/// already refused by the gate.
/// </summary>
public sealed class LinkIngress(
    AdmissionGate gate,
    IngressStore inbox,
    ICoordinationStore store,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    Facilitator.Kernel.Work.ApprovalIngress approvals,
    Facilitator.Kernel.Work.ArtifactIngress artifacts,
    Facilitator.Kernel.Work.ResultIngress results,
    Facilitator.Kernel.Work.MemoryIngress memory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validate + admit one raw envelope (gate → dedupe insert → dispatch) under a workload principal.</summary>
    public async Task<LinkAdmission> AdmitAsync(string rawBody, Facilitator.Kernel.Shared.Auth.LinkPrincipal workload, CancellationToken ct)
    {
        var screen = await gate.ScreenAsync(rawBody, workload, ct);
        if (screen.Problem is not null)
        {
            return new LinkAdmission(screen.Problem, null, null);
        }

        var root = screen.Root;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), EnvelopeReader.SourceOf(root), EnvelopeReader.EventIdOf(root), screen.CommandId, rawBody,
            null, screen.IsCommand ? "pending" : "processed", screen.IsCommand ? null : now, ct);
        metrics.LinkReceived();
        if (duplicate)
        {
            // A duplicate request_upload re-attaches the still-live ticket so a
            // retried command can still find its upload location.
            object? ticketData = null;
            if (screen.Type == "org.facilitator.command.artifact.request_upload.v1" && screen.CommandId is { } cid)
            {
                var live = await artifacts.TicketForLinkCommandAsync(cid, ct);
                ticketData = live is null ? null : Facilitator.Kernel.Work.ArtifactEndpoints.Ticket(live);
            }

            // Plan 16 law: a duplicate retain re-runs the idempotent broker
            // path (content-hash dedupe returns the stored identity +
            // duplicate:true); a duplicate recall re-runs (a recall is a read
            // whose usage rows are per-delivery by design).
            object? memoryData = null;
            if (screen.Type == Facilitator.Kernel.Work.MemoryIngress.RetainType
                || screen.Type == Facilitator.Kernel.Work.MemoryIngress.RecallType)
            {
                var arm = screen.Type == Facilitator.Kernel.Work.MemoryIngress.RetainType
                    ? await memory.HandleRetainAsync(root, screen.Principal, ct)
                    : await memory.HandleRecallAsync(root, screen.Principal, ct);
                memoryData = arm.Data;
            }

            return new LinkAdmission(null, "duplicate", inboxId, ticketData ?? memoryData);
        }

        if (!screen.IsCommand)
        {
            if (screen.Type is "org.facilitator.event.approval.required.v1" or "org.facilitator.event.approval.revoked.v1")
            {
                var arm = await approvals.HandleEventAsync(root, screen.Type, workload.EndpointId, ct);
                if (arm.Refused)
                {
                    await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                    return new LinkAdmission(arm.Problem, null, null);
                }

                metrics.InboxProcessed();
                return new LinkAdmission(null, "accepted", inboxId);
            }

            if (screen.Type == "org.facilitator.event.artifact.available.v1")
            {
                var arm = await artifacts.HandleEventAsync(root, ct);
                if (arm.Refused)
                {
                    await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                    return new LinkAdmission(arm.Problem, null, null);
                }

                await inbox.MarkProcessedAsync(inboxId, null, ct);
                metrics.InboxProcessed();
                return new LinkAdmission(null, "accepted", inboxId);
            }

            if (screen.Type == Facilitator.Kernel.Work.ResultIngress.CompletedType)
            {
                // Plan 15: a completed event carrying a manifest ref records
                // the manifest; a refusal settles the inbox with the refused
                // status, mirroring the artifact.available arm.
                var arm = await results.HandleCompletedAsync(root, screen.Principal, ct);
                if (arm.Refused)
                {
                    await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                    return new LinkAdmission(arm.Problem, null, null);
                }

                await inbox.MarkProcessedAsync(inboxId, null, ct);
                metrics.InboxProcessed();
                return new LinkAdmission(null, "accepted", inboxId);
            }

            if (screen.Type == Facilitator.Kernel.Work.MemoryIngress.CandidateType
                || screen.Type == Facilitator.Kernel.Work.MemoryIngress.UsedType)
            {
                // Plan 16: the frozen memory events record their ledger effect;
                // a refusal settles the inbox with the refused status, mirroring
                // the manifest arm. Memory outages never refuse (accepted path).
                // Events carry no evidence, so the authority is the enrolled
                // workload itself — its team owns the landed entry.
                var eventPrincipal = new Facilitator.Kernel.Shared.Evidence.EffectivePrincipal(
                    workload, null, "workload-only");
                var arm = screen.Type == Facilitator.Kernel.Work.MemoryIngress.CandidateType
                    ? await memory.HandleCandidateAsync(root, eventPrincipal, ct)
                    : await memory.HandleUsedAsync(root, eventPrincipal, ct);
                if (arm.Refused)
                {
                    await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                    return new LinkAdmission(arm.Problem, null, null);
                }

                await inbox.MarkProcessedAsync(inboxId, null, ct);
                metrics.InboxProcessed();
                return new LinkAdmission(null, "accepted", inboxId);
            }

            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId);
        }

        if (screen.Type == "org.facilitator.command.approval.respond.v1")
        {
            // The gate already evaluated the evidence + audited
            // authorization.evaluated:link; the ledger requires a delegated
            // human identity (law D4) — workload-only denies 403.
            var delegated = screen.Principal?.Delegated;
            var arm = await approvals.HandleRespondAsync(
                root, delegated?.Subject, delegated?.Groups, null, mintEvidence: false, ct);
            if (arm.Refused)
            {
                await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                return new LinkAdmission(arm.Problem, null, null);
            }

            await inbox.MarkProcessedAsync(inboxId, null, ct);
            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId);
        }

        if (screen.Type == "org.facilitator.command.approval.consume.v1")
        {
            // Plan 12: the Gateway that recorded the approval consumes it
            // before applying; the ledger gates on row ownership (a foreign
            // endpoint sees 404), non-gateway roles deny 403.
            var arm = await approvals.HandleConsumeAsync(
                root, workload.EndpointId, string.Equals(workload.Role, "gateway", StringComparison.Ordinal), ct);
            if (arm.Refused)
            {
                await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                return new LinkAdmission(arm.Problem, null, null);
            }

            await inbox.MarkProcessedAsync(inboxId, null, ct);
            metrics.InboxProcessed();
            return new LinkAdmission(null, "consumed", inboxId);
        }

        if (screen.Type == "org.facilitator.command.artifact.request_upload.v1")
        {
            // The gate already evaluated the evidence + audited the principal;
            // allocation is the ledger's. The ticket rides back in-band on the
            // ack (data), never as a pushed event.
            var arm = await artifacts.HandleRequestUploadAsync(root, screen.Principal, ct);
            if (arm.Refused)
            {
                await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                return new LinkAdmission(arm.Problem, null, null);
            }

            await inbox.MarkProcessedAsync(inboxId, null, ct);
            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId,
                Facilitator.Kernel.Work.ArtifactEndpoints.Ticket(arm.Ticket!));
        }

        if (screen.Type == "org.facilitator.command.artifact.confirm_upload.v1")
        {
            // D9: the link token is the actor proof; the arm runs the ledger's
            // re-hash law and answers settled / refused like the upload twin.
            var arm = await artifacts.HandleConfirmUploadAsync(root, screen.Principal, ct);
            if (arm.Refused)
            {
                await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                return new LinkAdmission(arm.Problem, null, null);
            }

            await inbox.MarkProcessedAsync(inboxId, null, ct);
            metrics.InboxProcessed();
            return new LinkAdmission(null, "confirmed", inboxId);
        }

        if (screen.Type == Facilitator.Kernel.Work.MemoryIngress.RetainType
            || screen.Type == Facilitator.Kernel.Work.MemoryIngress.RecallType)
        {
            // Plan 16 D1: the retain receipt / recall response ride the ack
            // in-band (the request_upload ticket precedent).
            var arm = screen.Type == Facilitator.Kernel.Work.MemoryIngress.RetainType
                ? await memory.HandleRetainAsync(root, screen.Principal, ct)
                : await memory.HandleRecallAsync(root, screen.Principal, ct);
            if (arm.Refused)
            {
                await inbox.MarkProcessedAsync(inboxId, arm.Status, ct);
                return new LinkAdmission(arm.Problem, null, null);
            }

            await inbox.MarkProcessedAsync(inboxId, null, ct);
            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId, arm.Data);
        }

        var workId = EnvelopeReader.WorkIdOf(root);
        if (screen.Type == "org.facilitator.command.work.steer.v1" && workId is not null)
        {
            var data = root.GetProperty("data");
            var directive = data.GetProperty("directive").GetString() ?? string.Empty;
            var expectedVersion = data.TryGetProperty("expectedContextVersion", out var ev) && ev.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            var steerStatus = "accepted";
            using (KernelActivity.Source.StartActivity("ingress.process", ActivityKind.Internal))
            {
                var steer = await store.ApplySteerAsync(workId.Value, directive, screen.CommandId!, expectedVersion, ct);
                var error = steer switch
                {
                    SteerResult.Ok => null,
                    SteerResult.Conflict => "version-conflict",
                    SteerResult.Terminal => "terminal-state",
                    SteerResult.Duplicate => null,
                    _ => "work-not-found",
                };
                await inbox.MarkProcessedAsync(inboxId, error, ct);
                metrics.InboxProcessed();
                steerStatus = steer is SteerResult.Duplicate ? "duplicate" : "accepted";
                if (steer is SteerResult.Conflict conflict)
                {
                    return new LinkAdmission(ProblemErrors.VersionConflict(conflict.CurrentVersion), null, null);
                }

                if (steer is SteerResult.Terminal)
                {
                    return new LinkAdmission(ProblemErrors.InvalidTransition("terminal→steer"), null, null);
                }

                if (steer is SteerResult.NotFound)
                {
                    return new LinkAdmission(ProblemErrors.WorkNotFound(workId.Value), null, null);
                }
            }

            return new LinkAdmission(null, steerStatus, inboxId);
        }

        // Known command with no link-side business effect: processed + typed
        // timeline note when the work is known (Work's precedent: typed, not silent).
        if (workId is not null)
        {
            await inbox.AppendTimelineNoteAsync(workId.Value, "command.accepted", "link",
                JsonSerializer.Serialize(new Dictionary<string, string?> { ["type"] = screen.Type, ["commandId"] = screen.CommandId }, Json),
                screen.CommandId, ct);
        }

        await inbox.MarkProcessedAsync(inboxId, null, ct);
        metrics.InboxProcessed();
        return new LinkAdmission(null, "accepted", inboxId);
    }

    /// <summary>SHA-256 of a capability manifest (digest the enrollment record stores).</summary>
    public static string DigestOf(string manifestJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();
}
