using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Dispatch;
using Facilitator.Kernel.Ingress;
using Facilitator.Kernel.Persistence;

using Microsoft.AspNetCore.Mvc;

using Ofac.Protocol.Validation;

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
/// (identical semantics on both transports). Reuses Plan 02's
/// <see cref="IngressStore"/> inbox dedupe insert verbatim: persist the inbox
/// row BEFORE answering accepted (S:301); duplicate replays answer
/// <c>duplicate</c>; unknown commands fail closed with a typed
/// <c>unsupported</c> problem + a failed audit row (S:305).
/// </summary>
public sealed class LinkIngress(
    IngressStore inbox,
    ICoordinationStore store,
    EnvelopeValidator validator,
    KernelMetrics metrics,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validate + admit one raw envelope. Size gate (413 @262144) →
    /// envelope validation (400 envelope/schema-violation) → receipt-class
    /// frames are 400 (receipts travel via Receipt/receipt endpoint) →
    /// work.assign FROM a link is illegal (422 unsupported — assign is
    /// kernel→link only) → dedupe insert → work.steer runs the SAME
    /// ApplySteerAsync path as Plan 02 ingress; the other six known commands
    /// settle as a processed no-op; event-class settles accepted with no side
    /// effects yet; unknown command class fails closed 422 + failed audit row.
    /// </summary>
    public async Task<LinkAdmission> AdmitAsync(string rawBody, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(rawBody);
        if (bytes > IngressProcessor.MaxBodyBytes)
        {
            return new LinkAdmission(ProblemErrors.PayloadTooLarge(bytes), null, null);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new LinkAdmission(ProblemErrors.SchemaViolation("body is not valid JSON"), null, null);
        }

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        if (type.StartsWith("org.facilitator.receipt.", StringComparison.Ordinal))
        {
            return new LinkAdmission(
                ProblemErrors.EnvelopeViolation("receipt-class frames travel via the Receipt method / receipt endpoint, not envelopes"),
                null, null);
        }

        var verdict = validator.Validate(root, bytes);
        if (!verdict.Ok)
        {
            if (verdict.ErrorClass == "unknown-command")
            {
                // §5.5: fail closed, but persist the audit row.
                var (auditId, _) = await inbox.InsertInboxAsync(
                    Guid.NewGuid(), SourceOf(root), EventIdOf(root), null, rawBody, null, "failed", null, ct);
                _ = auditId;
                metrics.LinkReceived();
                return new LinkAdmission(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"), null, null);
            }

            return new LinkAdmission(verdict.ErrorClass switch
            {
                "payload-too-large" => ProblemErrors.PayloadTooLarge(bytes),
                "schema-violation" => ProblemErrors.SchemaViolation(verdict.Message ?? "schema violation"),
                _ => ProblemErrors.EnvelopeViolation(verdict.Message ?? "envelope violation"),
            }, null, null);
        }

        var isCommand = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal);
        if (isCommand && !KnownCommands.All.Contains(type))
        {
            var (auditId, _) = await inbox.InsertInboxAsync(
                Guid.NewGuid(), SourceOf(root), EventIdOf(root), null, rawBody, null, "failed", null, ct);
            _ = auditId;
            metrics.LinkReceived();
            return new LinkAdmission(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"), null, null);
        }

        // assign is kernel→link only: a link posting one is a protocol error.
        if (type == "org.facilitator.command.work.assign.v1")
        {
            metrics.LinkReceived();
            return new LinkAdmission(
                ProblemErrors.Unsupported("work.assign is kernel→link only; a link endpoint may not post it"), null, null);
        }

        var commandId = isCommand
            && root.GetProperty("data").TryGetProperty("commandId", out var cid)
            && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        var state = isCommand ? "pending" : "processed";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), SourceOf(root), EventIdOf(root), commandId, rawBody,
            null, state, isCommand ? null : now, ct);
        metrics.LinkReceived();
        if (duplicate)
        {
            return new LinkAdmission(null, "duplicate", inboxId);
        }

        if (!isCommand)
        {
            metrics.InboxProcessed();
            return new LinkAdmission(null, "accepted", inboxId);
        }

        var workId = WorkIdOf(root);
        if (type == "org.facilitator.command.work.steer.v1" && workId is not null)
        {
            var data = root.GetProperty("data");
            var directive = data.GetProperty("directive").GetString() ?? string.Empty;
            var expectedVersion = data.TryGetProperty("expectedContextVersion", out var ev) && ev.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            var steerStatus = "accepted";
            using (KernelActivity.Source.StartActivity("ingress.process", ActivityKind.Internal))
            {
                var steer = await store.ApplySteerAsync(workId.Value, directive, commandId!, expectedVersion, ct);
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
        // timeline note when the work is known (Plan 02 precedent: typed, not silent).
        if (workId is not null)
        {
            await inbox.AppendTimelineNoteAsync(workId.Value, "command.accepted", "link",
                JsonSerializer.Serialize(new Dictionary<string, string?> { ["type"] = type, ["commandId"] = commandId }, Json),
                commandId, ct);
        }

        await inbox.MarkProcessedAsync(inboxId, null, ct);
        metrics.InboxProcessed();
        return new LinkAdmission(null, "accepted", inboxId);
    }

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

    /// <summary>SHA-256 of a capability manifest (digest the enrollment record stores).</summary>
    public static string DigestOf(string manifestJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();
}
