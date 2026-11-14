using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The shadow reconciliation report: the plane's would-do (step-log rows
/// with status <c>shadow</c>) diffed against the arm's ACTUAL effects,
/// keyed on the trigger's delivery identity — never a timing join. The
/// ledger row (<c>provider_events</c>) records the arm's judgment
/// (<c>work/{successor}:input</c> when it forked) for a delivery; the
/// plane's trigger row copies the same <c>providerDeliveryId</c> inside
/// its envelope; the instance identity derives from the trigger row's own
/// id. So each shadow run is compared to the real effects of the very
/// delivery that started it: successor identity and objective, the
/// successor link (kind + note), the binding's new target, and the reply
/// command id + body.
/// </summary>
public sealed class WorkflowShadowReport(
    IDbContextFactory<FacilitatorDbContext> contextFactory)
{
    /// <summary>The only trigger this report reconciles (Phase 2 scope).</summary>
    public const string ReviewType = "org.facilitator.event.provider.review.v1";

    /// <summary>One reconciled review: <c>match</c> is true exactly when
    /// every field agrees (or neither side acted); <c>diffs</c> names each
    /// disagreeing field with both values.</summary>
    public sealed record Row(
        string Provider,
        string DeliveryId,
        string Judgment,
        Guid? InstanceId,
        bool PlaneWouldAct,
        bool ArmActed,
        bool Match,
        IReadOnlyList<string> Diffs);

    public async Task<IReadOnlyList<Row>> ReportAsync(DateTime since, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Ledger rows with a judgment, since the window — the arm's side.
        var ledger = await db.ProviderEvents.AsNoTracking()
            .Where(e => e.ReceivedAt >= since && e.VerifiedReference != null)
            .OrderBy(e => e.ReceivedAt)
            .Select(e => new { e.Provider, e.ProviderDeliveryId, e.VerifiedReference })
            .ToListAsync(ct);

        // The plane's trigger rows for the same deliveries (envelope copy
        // of providerDeliveryId — the delivery identity is the join key).
        var triggers = await db.Outbox.AsNoTracking()
            .Where(o => o.RecipientEndpointId == WorkflowTrigger.Recipient
                        && EF.Functions.Like(o.CommandId, "wf-trigger:%")
                        && o.EventType == ReviewType)
            .Select(o => new { o.OutboxId, o.CommandId, o.Envelope, o.CreatedAt, o.WorkId })
            .Where(o => o.CreatedAt >= since)
            .ToListAsync(ct);

        var rows = new List<Row>();
        foreach (var ledgerRow in ledger)
        {
            if (ledgerRow.ProviderDeliveryId is not { Length: > 0 } deliveryId)
            {
                continue;
            }

            var judgment = ledgerRow.VerifiedReference!;

            // Only review judgments concern this report; the review arm's
            // fork judgment is `work/{successor}:input`, its live-item
            // judgments `steered:`/`review:`/`work/{id}`.
            foreach (var trigger in triggers.Where(t =>
                         DeliveryOf(t.Envelope) == deliveryId && ProviderOf(t.Envelope) == ledgerRow.Provider))
            {
                var defMatch = WorkflowTrigger.Decode(new Shared.Dispatch.OutboxRow(
                    trigger.OutboxId, WorkflowTrigger.Recipient, "trigger",
                    trigger.CommandId,
                    JsonDocument.Parse(trigger.Envelope).RootElement, 0, trigger.WorkId));
                if (defMatch is null)
                {
                    continue;
                }

                rows.Add(await ReconcileAsync(db, ledgerRow.Provider, deliveryId, judgment, defMatch, ct));
            }
        }

        return rows;
    }

    private static async Task<Row> ReconcileAsync(
        FacilitatorDbContext db,
        string provider,
        string deliveryId,
        string judgment,
        WorkflowTrigger trigger,
        CancellationToken ct)
    {
        var instanceId = WorkflowIdentity.InstanceIdFor(trigger.DefinitionId, trigger.DefinitionVersion, trigger.TriggerRowId);
        var logs = await db.WorkflowStepLog.AsNoTracking()
            .Where(l => l.InstanceId == instanceId)
            .OrderBy(l => l.Seq)
            .ToListAsync(ct);
        if (logs.Count == 0)
        {
            // The plane never ran this delivery (definition disabled and
            // unshadowed, or the row is still pending): the report says so
            // rather than inventing a match.
            return new Row(provider, deliveryId, judgment, instanceId, false, Forked(judgment),
                false, [$"no-plane-run: judgment '{judgment}'"]);
        }

        var guard = logs.FirstOrDefault(l => l.StepId == "guard-terminal");
        var fork = logs.FirstOrDefault(l => l.StepId == "fork");
        var guardHit = guard?.Output is { Length: > 0 } guardJson
                       && JsonDocument.Parse(guardJson).RootElement.TryGetProperty("hit", out var hit)
                       && hit.ValueKind == JsonValueKind.True;
        var planeWouldAct = guardHit && fork is not null;
        var armForked = Forked(judgment);
        var diffs = new List<string>();

        if (planeWouldAct != armForked)
        {
            diffs.Add($"decision: plane-would-act={planeWouldAct} arm-acted={armForked}");
        }

        if (planeWouldAct && armForked && fork?.Output is { Length: > 0 } outputJson)
        {
            var plan = JsonNode.Parse(outputJson)?["wouldDo"]?.AsObject();
            var successor = Guid.Parse(judgment.AsSpan("work/".Length, 36));

            // Deterministic-id law: the plane's successor id derives from
            // (instance, step); the arm's is a fresh guid, so the ids
            // DIFFER by design in shadow — what must agree is everything
            // derived from the subject + event: the objective, the link,
            // the reply sentence and command id modulo that substitution.
            var successorWork = await db.Works.AsNoTracking()
                .Where(w => w.WorkId == successor)
                .Select(w => new { w.Objective })
                .FirstOrDefaultAsync(ct);
            if (successorWork is not null && plan?["objective"]?.GetValue<string>() != successorWork.Objective)
            {
                diffs.Add($"objective: plane={plan?["objective"]?.GetValue<string>()} arm={successorWork.Objective}");
            }

            // The link row: predecessor is the STAMPED subject (the work
            // the review hit), successor the judgment's work.
            var link = trigger.SubjectWork is { } subjectWork
                ? await db.WorkLinks.AsNoTracking()
                    .Where(l => l.WorkId == subjectWork && l.RefWorkId == successor)
                    .Select(l => new { l.Kind, l.Note })
                    .FirstOrDefaultAsync(ct)
                : null;
            if (link is null)
            {
                diffs.Add("link: no successor link row on the review's subject work");
            }
            else if (link.Kind != plan?["linkKind"]?.GetValue<string>() || link.Note != plan?["linkNote"]?.GetValue<string>())
            {
                diffs.Add($"link: plane={plan?["linkKind"]}/{plan?["linkNote"]} arm={link.Kind}/{link.Note}");
            }

            // Binding follows the live item (terminal law): it must point
            // at the successor for the fork's conversation.
            var bound = await db.CommunicationBindings.AsNoTracking()
                .Where(b => b.WorkId == successor)
                .AnyAsync(ct);
            if (!bound)
            {
                diffs.Add("binding: not repointed to the successor");
            }

            // The reply: the arm's row carries the SAME deterministic
            // command id the plane computes.
            var reply = await db.Outbox.AsNoTracking()
                .Where(o => o.CommandId == $"successor:{successor:N}")
                .Select(o => o.Envelope)
                .FirstOrDefaultAsync(ct);
            var planeReplyId = plan?["replyCommandId"]?.GetValue<string>() ?? string.Empty;
            if (reply is null)
            {
                diffs.Add("reply: arm appended no successor reply row");
            }
            else
            {
                // Shape law: the plane's reply command id is
                // `successor:{its-successor:N}` — 32 hex characters on the
                // same prefix the arm used for the row we just found.
                if (!IsSuccessorCommandId(planeReplyId))
                {
                    diffs.Add($"reply-command-id: plane shape wrong: {planeReplyId}");
                }

                var armText = JsonDocument.Parse(reply).RootElement.GetProperty("data").GetProperty("text").GetString();
                var planeText = plan?["replyText"]?.GetValue<string>() ?? string.Empty;
                if (armText is null || !armText.StartsWith("Work ", StringComparison.Ordinal)
                    || !armText.Contains("is closed - this continues as work ", StringComparison.Ordinal))
                {
                    diffs.Add($"reply-text: arm sentence unexpected: {armText}");
                }
                else if (planeText.Length > 0 && SuccessorBlind(planeText) != SuccessorBlind(armText))
                {
                    diffs.Add($"reply-text: plane={planeText} arm={armText}");
                }
            }
        }

        return new Row(provider, deliveryId, judgment, instanceId, planeWouldAct, armForked, diffs.Count == 0, diffs);

        // Sentence comparison modulo the successor short id (the one
        // legitimate difference between a computed plan and an executed
        // fork while the arm mints its successor id randomly).
        static string SuccessorBlind(string text)
        {
            var marker = " as work ";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? text : text[..(at + marker.Length)];
        }
    }

    /// <summary>The arm acted on the fork path: its judgment names the
    /// successor work (`work/{successor}:input`) — any other review
    /// judgment (steered, review:action, work/{id}) means no fork.</summary>
    private static bool Forked(string judgment) =>
        judgment.StartsWith("work/", StringComparison.Ordinal)
        && judgment.Contains(":input", StringComparison.Ordinal);

    private static bool IsSuccessorCommandId(string commandId)
    {
        const string prefix = "successor:";
        if (!commandId.StartsWith(prefix, StringComparison.Ordinal) || commandId.Length != prefix.Length + 32)
        {
            return false;
        }

        foreach (var c in commandId[prefix.Length..])
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string? DeliveryOf(string envelope) =>
        TryDataString(envelope, "providerDeliveryId");

    private static string? ProviderOf(string envelope) =>
        TryDataString(envelope, "provider");

    private static string? TryDataString(string envelope, string key)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.TryGetProperty("data", out var data)
               && data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }
}
