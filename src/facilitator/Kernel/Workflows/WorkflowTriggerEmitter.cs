using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// Trigger matching law of the plane: the matcher is the
/// policy-file matcher law verbatim — exact, bare <c>*</c>, or a single
/// trailing <c>*</c> prefix-glob over dotted <c>data.*</c> paths; no
/// expression language. A definition contributes one trigger when its
/// <c>on.event</c> equals the wire type AND every <c>where</c> term matches
/// the envelope's <c>data</c>.
/// </summary>
public static class WorkflowMatcher
{
    /// <summary>One matcher term.</summary>
    public static bool Matches(JsonElement matcher, JsonElement? actual)
    {
        if (actual is null)
        {
            return false;
        }

        return matcher.ValueKind switch
        {
            JsonValueKind.String => MatchTerm(matcher.GetString()!, actual.Value),
            JsonValueKind.True => actual.Value.ValueKind == JsonValueKind.True,
            JsonValueKind.False => actual.Value.ValueKind == JsonValueKind.False,
            JsonValueKind.Number => actual.Value.ValueKind == JsonValueKind.Number
                                    && matcher.GetRawText() == actual.Value.GetRawText(),
            _ => false,
        };
    }

    private static bool MatchTerm(string term, JsonElement actual)
    {
        var actualText = actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => actual.GetRawText(),
            _ => null,
        };

        if (actualText is null)
        {
            return false;
        }

        if (term == "*")
        {
            return true;
        }

        return term.EndsWith("*", StringComparison.Ordinal)
            ? actualText.StartsWith(term[..^1], StringComparison.Ordinal)
            : string.Equals(term, actualText, StringComparison.Ordinal);
    }

    /// <summary>Resolve a dotted path (e.g. <c>data.action</c> or the
    /// step-relative <c>steps.s1.output.branch</c>) inside a context
    /// document. Absent ⇒ null (guards treat absent as no-match).</summary>
    public static JsonElement? ResolvePath(JsonElement context, string dottedPath)
    {
        JsonElement current = context;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}

/// <summary>
/// The subscription-set trigger emitter: after a wire event is accepted
/// (ingress) or produced (any kernel arm), the emitter appends one
/// <c>workflow.kernel</c> trigger row per enabled definition whose trigger
/// matches, and one per running/waiting instance whose pinned definition
/// waits on the event type (parked flows wake without anyone triggering on
/// the event). When the plane is disabled the emitter never appends — the
/// no-DLQ-noise law of the sink's boot flag.
/// </summary>
public sealed class WorkflowTriggerEmitter(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    WorkflowRuntime runtime,
    WorkflowSubscriptionRegistry subscriptions,
    TimeProvider timeProvider)
{
    /// <summary>Append trigger rows for one accepted wire event envelope on
    /// the caller's context (typically the arm's transaction — outbox law).
    /// Trigger rows seed their identity from their own row id (inbox dedupe
    /// already guarantees one accepted event = one trigger row).</summary>
    public async Task<int> EmitAsync(
        FacilitatorDbContext db,
        JsonElement envelope,
        CancellationToken ct) => await EmitCoreAsync(db, envelope, null, ct);

    /// <summary>The arm hook: a kernel arm that consumes a wire event feeds
    /// the plane from INSIDE its own transaction, stamping the subject work
    /// it acted on. The stamp is load-bearing, not an optimization: an arm
    /// may move the binding (the review fork repoints it to the successor),
    /// so a consumer that re-resolved the work at delivery time would see a
    /// different subject than the one the event hit.</summary>
    public async Task<int> EmitAsync(
        FacilitatorDbContext db,
        JsonElement envelope,
        Guid? subjectWork,
        CancellationToken ct) => await EmitCoreAsync(db, envelope, subjectWork, ct);

    /// <summary>The standalone leg for callers without an ambient
    /// transaction (the CloudEvent ingress after its inbox row settles):
    /// its own short transaction commits the trigger rows and notifies.</summary>
    public async Task<int> EmitOwnedAsync(JsonElement envelope, CancellationToken ct)
    {
        if (!runtime.Enabled)
        {
            return 0;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var appended = await EmitCoreAsync(db, envelope, null, ct);
        if (appended > 0)
        {
            await db.SaveChangesAsync(ct);
            await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        }

        return appended;
    }

    private async Task<int> EmitCoreAsync(
        FacilitatorDbContext db,
        JsonElement envelope,
        Guid? subjectWork,
        CancellationToken ct)
    {
        if (!runtime.Enabled)
        {
            return 0;
        }

        var type = envelope.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : string.Empty;
        if (type.Length == 0)
        {
            return 0;
        }

        // Subscription-set fast path: no definition triggers and no park
        // awaits this type — nothing to match, nothing to append.
        if (!await subscriptions.MayContainAsync(type, ct))
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var appended = 0;

        // 1) Enabled definitions triggering on this type (latest enabled
        //    version each), matcher-checked.
        var definitions = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.Enabled)
            .ToListAsync(ct);
        foreach (var byId in definitions.GroupBy(d => d.DefId, StringComparer.Ordinal))
        {
            var latest = byId.OrderByDescending(d => d.Version).First();
            using var doc = JsonDocument.Parse(latest.Payload);
            var on = doc.RootElement.GetProperty("on");
            if (!on.TryGetProperty("event", out var eventProp)
                || eventProp.GetString() != type
                || !WhereMatches(on, envelope))
            {
                continue;
            }

            AppendTrigger(db, latest.DefId, latest.Version, envelope, now,
                subjectWork ?? EnvelopeWorkId(envelope, db));
            appended++;
        }

        // 2) Parked instances whose pinned definition waits on this type
        //    (the wake leg — Phase 6 makes wakes first-class; the wait-step
        //    park already records the correlation key here).
        var waiting = await db.WorkflowInstances.AsNoTracking()
            .Where(i => i.Status == "waiting")
            .ToListAsync(ct);
        foreach (var instance in waiting)
        {
            var pinned = definitions.FirstOrDefault(d => d.DefId == instance.DefId && d.Version == instance.DefVersion)
                ?? await db.WorkflowDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DefId == instance.DefId && d.Version == instance.DefVersion, ct);
            if (pinned is null)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(pinned.Payload);
            var step = doc.RootElement.GetProperty("steps").EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("id").GetString() == instance.CurrentStep);
            if (step.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A park is a `wait` on its declared event, or an `approval`
            // (which parks on approval.decided by approval-id correlation).
            var awaitedType = step.TryGetProperty("wait", out var waitProp)
                ? waitProp.GetProperty("event").GetString()
                : step.TryGetProperty("approval", out _) && type == "org.facilitator.event.approval.decided.v1"
                    ? type
                    : null;
            if (awaitedType != type)
            {
                continue;
            }

            // Wake correlation: the parked step's correlation key must
            // appear in the envelope (data-scoped). No key match ⇒ the
            // event is unrelated to this park; nothing appended.
            if (ParkedCorrelationKey(db, instance) is not { Length: > 0 } key
                || !EnvelopeCarries(envelope, key))
            {
                continue;
            }

            // Deterministic per (instance, source event): the wire id of
            // the envelope, namespaced (external ids are not necessarily
            // guids); a redelivery of the same event recomputes the same
            // wake command id.
            var wireId = envelope.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var wakeSeed = WorkflowIdentity.Uuid5(
                WorkflowIdentity.TriggerNamespace, $"{instance.InstanceId:N}:{wireId}");
            AppendWake(db, instance.InstanceId, instance.PrimaryWorkId, wakeSeed, envelope, now);
            appended++;
        }

        return appended;
    }

    private static bool WhereMatches(JsonElement on, JsonElement envelope)
    {
        if (!on.TryGetProperty("where", out var where))
        {
            return true;
        }

        var data = envelope.TryGetProperty("data", out var d) ? d : default;
        foreach (var term in where.EnumerateObject())
        {
            var path = term.Name["data.".Length..];
            var actual = data.ValueKind == JsonValueKind.Object
                         && data.TryGetProperty(path, out var v)
                ? v
                : (JsonElement?)null;
            if (!WorkflowMatcher.Matches(term.Value, actual))
            {
                return false;
            }
        }

        return true;
    }

    // Correlation law (Phase 1): a park records its correlation key in the
    // step-log row's output (the wait's resolved `correlate` ref, the
    // approval's own id); the waking envelope must carry that exact string
    // somewhere in `data` — the plane stays free of expression machinery.
    internal static string? ParkedCorrelationKey(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance)
    {
        var parked = db.WorkflowStepLog.AsNoTracking()
            .Where(l => l.InstanceId == instance.InstanceId && l.StepId == instance.CurrentStep)
            .OrderByDescending(l => l.Seq)
            .FirstOrDefault();
        if (parked?.Output is not { Length: > 0 } outputJson)
        {
            return null;
        }

        using var output = JsonDocument.Parse(outputJson);
        return output.RootElement.TryGetProperty("correlationKey", out var keyElement)
               && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString()
            : null;
    }

    internal static bool EnvelopeCarries(JsonElement envelope, string key) =>
        envelope.TryGetProperty("data", out var data) && DataCarries(data, key);

    private static bool DataCarries(JsonElement data, string key)
    {
        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == key)
            {
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.Object && DataCarries(property.Value, key))
            {
                return true;
            }
        }

        return false;
    }

    internal static void AppendWake(
        FacilitatorDbContext db,
        Guid instanceId,
        Guid? workId,
        Guid sourceRowId,
        JsonElement envelope,
        DateTime now)
    {
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = $"wf-wake:{instanceId:N}:{sourceRowId:N}",
            RecipientEndpointId = WorkflowTrigger.Recipient,
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope.GetProperty("type").GetString()!,
            Envelope = envelope.GetRawText(),
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    /// <summary>The subject-work stamp for an unguided emit: the envelope's
    /// own data.workId, else the communication binding the arms use. The
    /// runner prefers the stamp over any lookup at delivery time.</summary>
    private static Guid? EnvelopeWorkId(JsonElement envelope, FacilitatorDbContext db)
    {
        var data = envelope.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("workId", out var wid) && wid.ValueKind == JsonValueKind.String
            && Guid.TryParse(wid.GetString(), out var direct))
        {
            return direct;
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.Object
            && reference.TryGetProperty("provider", out var provider)
            && reference.TryGetProperty("channelOrResourceId", out var channel)
            && reference.TryGetProperty("rootThreadId", out var root))
        {
            return db.CommunicationBindings.AsNoTracking()
                .Where(b =>
                    b.Provider == provider.GetString()
                    && b.Channel == channel.GetString()
                    && b.RootMessageId == root.GetString())
                .Select(b => b.WorkId)
                .FirstOrDefault();
        }

        return null;
    }

    private static void AppendTrigger(
        FacilitatorDbContext db,
        string defId,
        int version,
        JsonElement envelope,
        DateTime now,
        Guid? subjectWork)
    {
        // The emitter generates the row id itself: the instance identity
        // seeds from the TRIGGER ROW's own guid (wire-level dedupe already
        // happened at the inbox, so one accepted event = one trigger row =
        // one instance).
        var outboxId = Guid.NewGuid();
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = outboxId,
            CommandId = WorkflowIdentity.TriggerCommandId(defId, version, outboxId),
            RecipientEndpointId = WorkflowTrigger.Recipient,
            AggregateType = "work",
            AggregateId = null,
            EventType = envelope.GetProperty("type").GetString()!,
            Envelope = envelope.GetRawText(),
            WorkId = subjectWork,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }
}
