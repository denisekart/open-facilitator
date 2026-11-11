using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Teams;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Notifications;

/// <summary>What a caller asks the notification ledger to deliver: an
/// audience, a level, the text, and the address ladder most-specific-first.</summary>
public sealed record NotificationRequest(
    string Audience,
    string Text,
    string Level = "notice",
    Guid? WorkId = null,
    string? TeamSlug = null,
    string? Actor = null,
    ConversationTarget? Target = null,
    IReadOnlyList<ConversationTarget>? Ladder = null,
    string? CommandId = null,
    int CorrectionWindowMinutes = 0);

/// <summary>
/// The single human-egress path (A6, B4, B5, D5): every notice the kernel sends a
/// human lands in <c>facilitator.notifications</c> AND its outbox row in one
/// transaction — the ledger is the promise, the outbox row the attempt. The
/// <see cref="EgressResolver"/> picks the rung BEFORE the row is written, so
/// an undeliverable notice is recorded as undeliverable with its reason (P5),
/// not as a silently dropped message. An address naming a human by NAME that
/// matches several roster members produces the disambiguation question
/// instead of a delivery (B5: a silent pick is a defect). The wire command is
/// the same <c>command.communication.reply.v1</c> every adapter already
/// speaks, so this changes delivery truth, not the protocol.
/// </summary>
public sealed class NotificationLedger(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EgressResolver resolver,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider)
{
    /// <summary>The outbox command-id prefix every notification row carries
    /// (receipt-side lookup key for the obsolete transition).</summary>
    public const string CommandPrefix = "notify:";

    /// <summary>Record + enqueue one notification (caller's transaction).
    /// Returns the ledger row id and the state it landed in: a rung was found
    /// (<c>sent</c>/<c>fell-back</c>) or nothing can deliver (
    /// <c>undeliverable</c> — the abandonment is visible with its reason).</summary>
    public async Task<(Guid NotificationId, string State)> EmitAsync(
        FacilitatorDbContext db, NotificationRequest request, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var teamId = await TeamIdAsync(db, request.TeamSlug, ct);

        // Actor audience addressed BY NAME (B5): resolve across the roster.
        // Two matches is not a coin flip — the disambiguation question rides
        // the same ladder; no match records the dead end.
        if (request.Audience == "actor"
            && request.Actor is { Length: > 0 } addressee
            && !addressee.Contains(':', StringComparison.Ordinal))
        {
            var resolution = await ActorDirectory.ResolveAsync(db, request.TeamSlug, addressee, ct);
            if (resolution.Ambiguous)
            {
                return await EmitAsync(db, request with
                {
                    Audience = "team",
                    Text = ActorDirectory.DisambiguationText(addressee, resolution.Candidates),
                    Actor = null,
                }, ct);
            }

            if (!resolution.Resolved)
            {
                return await RecordUndeliverableAsync(
                    db, request, teamId, now,
                    $"'{addressee}' matches no team member", ct);
            }

            request = request with { Actor = resolution.Candidates[0] };
        }

        var row = new NotificationEntity
        {
            NotificationId = Guid.NewGuid(),
            WorkId = request.WorkId,
            Audience = request.Audience,
            TeamId = teamId,
            Actor = request.Actor,
            Level = request.Level,
            Text = request.Text,
            Target = request.Target is { } declared ? TargetJson(declared) : "{}",
            Fallback = LadderJson(request, request.Target),
            State = "pending",
            CorrectionWindowEndsAt = request.CorrectionWindowMinutes > 0
                ? now.AddMinutes(request.CorrectionWindowMinutes)
                : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Resolve BEFORE writing: the row must state its truth at birth — a
        // rung to send, or the reason nothing can.
        var answer = await resolver.ResolveAsync(row, ct);
        if (answer.Rung is null)
        {
            row.State = "undeliverable";
            row.Reason = answer.Reason;
            db.Notifications.Add(row);
            return (row.NotificationId, row.State);
        }

        var fellBack = request.Target is not null
            && !string.Equals(
                answer.Rung.Target.Channel ?? string.Empty,
                request.Target.Channel ?? string.Empty,
                StringComparison.Ordinal);
        row.Target = TargetJson(answer.Rung.Target);
        row.State = fellBack ? "fell-back" : "pending";
        row.Reason = fellBack ? answer.Reason : null;
        db.Notifications.Add(row);

        var commandId = request.CommandId is { Length: > 0 } provided
            ? provided
            : $"{CommandPrefix}{row.NotificationId:N}";
        var envelope = ProviderEventStore.ReplyEnvelope(
            answer.Rung.Target, request.Text, commandId,
            request.WorkId is { } w ? $"work/{w}" : $"notification/{row.NotificationId}", now);
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        // The attempt rides with the row atomically: a first-choice rung is
        // 'sent', a fallback rung 'fell-back' (its reason names why).
        if (!fellBack)
        {
            row.State = "sent";
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = $"link.{answer.Rung.EndpointId}",
            AggregateType = "work",
            AggregateId = request.WorkId,
            EventType = envelope["type"]!.GetValue<string>(),
            CommandId = commandId,
            Envelope = json,
            WorkId = request.WorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
        return (row.NotificationId, row.State);
    }

    /// <summary>Out-of-transaction convenience: own context, own save.</summary>
    public async Task<(Guid NotificationId, string State)> EmitAsync(NotificationRequest request, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var result = await EmitAsync(db, request, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Vanished address (F5): a delivery receipt that reports an
    /// unmappable target marks the notification obsolete and names the
    /// provider+channel on the work timeline. The work and its history are
    /// untouched — the address died, not the item.</summary>
    public Task<bool> MarkObsoleteAsync(
        FacilitatorDbContext db, string commandId, string reason, DateTime now, CancellationToken ct)
        => MarkObsoleteStaticAsync(db, commandId, reason, now, ct);

    /// <summary>Receipt-side entry point (F5): own context, keyed by the
    /// outbox command id the rejected delivery carried.</summary>
    public async Task<bool> MarkObsoleteAsync(string commandId, string reason, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var changed = await MarkObsoleteStaticAsync(
            db, commandId, reason, timeProvider.GetUtcNow().UtcDateTime, ct);
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    /// <summary>The receipt hook (F5), called inside the ack transaction:
    /// a PERMANENT rejection whose reason names an unmappable address
    /// (`target-missing-channel`, `unknown-post`, ...) retires the address,
    /// not the item. Transient rejections keep retrying and never touch the
    /// ledger row.</summary>
    public static async Task ApplyObsoleteOnRejectAsync(
        FacilitatorDbContext db, Guid outboxId, string receiptJson, DateTime now, CancellationToken ct)
    {
        var reason = UnmappableRejectionReason(receiptJson);
        if (reason is null)
        {
            return;
        }

        var commandId = await db.Outbox.AsNoTracking()
            .Where(o => o.OutboxId == outboxId)
            .Select(o => o.CommandId)
            .FirstOrDefaultAsync(ct);
        if (commandId is { Length: > 0 } id && id.StartsWith(CommandPrefix, StringComparison.Ordinal)
            && await MarkObsoleteStaticAsync(db, id, $"address-obsolete: {reason}", now, ct))
        {
            // The ack path's own SaveChanges ran before this hook (the
            // attempt-open leg): the retirement flushes here, in the tx.
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The unmappable-address vocabulary the adapters emit (the
    /// adapter's own words: a channel/post that is gone, not a bad payload,
    /// not a transient refusal).</summary>
    internal static string? UnmappableRejectionReason(string receiptJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(receiptJson);
            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() != "rejected")
            {
                return null;
            }

            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return reason is "target-missing-channel" or "unknown-post" or "target-missing-post" ? reason : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Cadence truth for one conversation (D1): the effective
    /// <c>notifications</c> payload resolved channel → team → system through
    /// the <see cref="EffectiveConfigResolver"/>, with the flat
    /// <c>replyVsReaction</c> switch read out of it (the `default` block of a
    /// scope-layered document counts as the layer's own value). One resolver
    /// serves the slash plane, the decode sink, and this ledger.</summary>
    public static async Task<string> CadenceAsync(
        FacilitatorDbContext db, string? teamSlug, string? channel, string fallback, CancellationToken ct)
    {
        var answer = await EffectiveConfigResolver.ResolveInTransactionAsync(db, teamSlug, channel, "notifications", ct);
        if (answer is null)
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(answer.Payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                root = d;
            }

            if (root.TryGetProperty("replyVsReaction", out var v) && v.GetString() is "reply" or "reaction")
            {
                return v.GetString()!;
            }
        }
        catch (JsonException)
        {
            // fall through to the caller's default
        }

        return fallback;
    }

    private static async Task<Guid?> TeamIdAsync(FacilitatorDbContext db, string? teamSlug, CancellationToken ct)
    {
        if (teamSlug is not { Length: > 0 } slug)
        {
            return null;
        }

        var found = await db.Teams.AsNoTracking().Where(t => t.Slug == slug).Select(t => t.TeamId).FirstOrDefaultAsync(ct);
        return found == Guid.Empty ? null : found;
    }

    private static async Task<(Guid NotificationId, string State)> RecordUndeliverableAsync(
        FacilitatorDbContext db, NotificationRequest request, Guid? teamId, DateTime now, string reason, CancellationToken ct)
    {
        var row = new NotificationEntity
        {
            NotificationId = Guid.NewGuid(),
            WorkId = request.WorkId,
            Audience = request.Audience,
            TeamId = teamId,
            Actor = request.Actor,
            Level = request.Level,
            Text = request.Text,
            Target = request.Target is { } declared ? TargetJson(declared) : "{}",
            Fallback = LadderJson(request, request.Target),
            State = "undeliverable",
            Reason = reason,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ = ct;
        db.Notifications.Add(row);
        return (row.NotificationId, row.State);
    }

    private static async Task<bool> MarkObsoleteStaticAsync(
        FacilitatorDbContext db, string commandId, string reason, DateTime now, CancellationToken ct)
    {
        var id = commandId.StartsWith(CommandPrefix, StringComparison.Ordinal)
            ? commandId[CommandPrefix.Length..]
            : string.Empty;
        var row = !Guid.TryParseExact(id, "N", out var notificationId)
            ? null
            : await db.Notifications.FirstOrDefaultAsync(n => n.NotificationId == notificationId, ct);
        if (row is null || row.State is "obsolete" or "undeliverable")
        {
            return false;
        }

        row.State = "obsolete";
        row.Reason = reason;
        row.UpdatedAt = now;
        if (row.WorkId is { } workId)
        {
            var version = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstOrDefaultAsync(ct);
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = version,
                Kind = "address.obsolete",
                Actor = "kernel:egress",
                Detail = new JsonObject
                {
                    ["notificationId"] = row.NotificationId.ToString(),
                    ["provider"] = Field(row.Target, "provider"),
                    ["channel"] = Field(row.Target, "channelOrResourceId"),
                    ["reason"] = reason,
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        return true;
    }

    private static string TargetJson(ConversationTarget target) =>
        new JsonObject
        {
            ["provider"] = target.Provider,
            ["tenantOrOrganizationId"] = target.Tenant,
            ["channelOrResourceId"] = target.Channel,
            ["rootThreadId"] = target.RootThreadId,
            ["parentMessageId"] = target.ParentMessageId,
            ["subjectRef"] = target.SubjectRef,
            ["conversationKind"] = target.ConversationKind,
            ["actorProviderAccountId"] = target.ActorProviderAccountId,
        }.ToJsonString();

    private static string LadderJson(NotificationRequest request, ConversationTarget? target)
    {
        var ladder = new JsonArray();
        foreach (var rung in request.Ladder ?? [])
        {
            if (target is null || rung.Channel != target.Channel || rung.ConversationKind != target.ConversationKind)
            {
                ladder.Add(JsonNode.Parse(TargetJson(rung)));
            }
        }

        return ladder.ToJsonString();
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
