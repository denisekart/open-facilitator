using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>What the admission gate hands to the dispatch half of <see cref="LinkIngress"/>.</summary>
/// <param name="Problem">Refusal to answer (audit row already persisted when one was needed); null = admitted.</param>
/// <param name="Root">Parsed envelope, when it parsed.</param>
/// <param name="Type">Envelope <c>type</c> (empty when absent/non-string).</param>
/// <param name="IsCommand">Type is a command-class CloudEvent.</param>
/// <param name="CommandId"><c>data.commandId</c> for commands.</param>
public sealed record AdmissionVerdict(ProblemDetails? Problem, JsonElement Root, string Type, bool IsCommand, string? CommandId);

/// <summary>
/// The validation prefix of link admission, shared by the hub <c>Envelope</c>
/// method and <c>POST /api/link/v1/events</c> via <see cref="LinkIngress"/>:
/// size gate → parse → receipt-class refusal → envelope validation →
/// known-command gate → assign-forbidden. Fail-closed refusals persist their
/// failed audit inbox row (S:305) here; dedupe + dispatch stay in
/// <see cref="LinkIngress"/>.
/// </summary>
public sealed class AdmissionGate(
    IngressStore inbox,
    EnvelopeValidator validator,
    KernelMetrics metrics)
{
    /// <summary>Screen one raw envelope; the verdict carries either a refusal or the parsed fields.</summary>
    public async Task<AdmissionVerdict> ScreenAsync(string rawBody, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(rawBody);
        if (bytes > RequestBodies.MaxBodyBytes)
        {
            return Refused(ProblemErrors.PayloadTooLarge(bytes));
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Refused(ProblemErrors.SchemaViolation("body is not valid JSON"));
        }

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        if (type.StartsWith("org.facilitator.receipt.", StringComparison.Ordinal))
        {
            return Refused(ProblemErrors.EnvelopeViolation(
                "receipt-class frames travel via the Receipt method / receipt endpoint, not envelopes"));
        }

        var verdict = validator.Validate(root, bytes);
        if (!verdict.Ok)
        {
            if (verdict.ErrorClass == "unknown-command")
            {
                // §5.5: fail closed, but persist the audit row.
                await AuditAsync(root, rawBody, ct);
                return Refused(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"));
            }

            return Refused(verdict.ErrorClass switch
            {
                "payload-too-large" => ProblemErrors.PayloadTooLarge(bytes),
                "schema-violation" => ProblemErrors.SchemaViolation(verdict.Message ?? "schema violation"),
                _ => ProblemErrors.EnvelopeViolation(verdict.Message ?? "envelope violation"),
            });
        }

        var isCommand = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal);
        if (isCommand && !KnownCommands.All.Contains(type))
        {
            await AuditAsync(root, rawBody, ct);
            return Refused(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"));
        }

        // assign is kernel→link only: a link posting one is a protocol error.
        if (type == "org.facilitator.command.work.assign.v1")
        {
            metrics.LinkReceived();
            return Refused(ProblemErrors.Unsupported("work.assign is kernel→link only; a link endpoint may not post it"));
        }

        var commandId = isCommand
            && root.GetProperty("data").TryGetProperty("commandId", out var cid)
            && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        return new AdmissionVerdict(null, root, type, isCommand, commandId);
    }

    private async Task AuditAsync(JsonElement root, string rawBody, CancellationToken ct)
    {
        await inbox.InsertInboxAsync(
            Guid.NewGuid(), EnvelopeReader.SourceOf(root), EnvelopeReader.EventIdOf(root), null, rawBody, null, "failed", null, ct);
        metrics.LinkReceived();
    }

    private static AdmissionVerdict Refused(ProblemDetails problem)
    {
        var _ = problem;
        return new AdmissionVerdict(problem, default, string.Empty, false, null);
    }
}
