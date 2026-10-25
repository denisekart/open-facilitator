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
public sealed record LinkAdmission(ProblemDetails? Problem, string? Status, Guid? InboxId)
{
    /// <summary>HTTP result equivalent of this verdict (OpenAPI: 202 + receipt body, else typed problem).</summary>
    public IResult ToResult(DateTime processedAt) =>
        Problem is not null ? Results.Problem(Problem)
        : Status == "duplicate" ? Results.Ok(new { status = "duplicate", inboxId = InboxId })
        : Results.Accepted(null, new { deliveryId = InboxId!.Value.ToString("N"), status = Status, processedAt });
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
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validate + admit one raw envelope (gate → dedupe insert → dispatch).</summary>
    public async Task<LinkAdmission> AdmitAsync(string rawBody, CancellationToken ct)
    {
        var screen = await gate.ScreenAsync(rawBody, ct);
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
            return new LinkAdmission(null, "duplicate", inboxId);
        }

        if (!screen.IsCommand)
        {
            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId);
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
