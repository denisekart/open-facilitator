using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Routing;

/// <summary>
/// The routing funnel: the ONLY producer of <c>work.assign</c> rows.
/// Sits on the dispatcher's <c>routing</c> channel — the same
/// <c>FOR UPDATE SKIP LOCKED</c> + lease choke point as every other sink, so
/// replicas route one intent at a time without a second queue. One pass of an
/// <see cref="RoutingIntent"/>:
/// candidates = live <c>link_sessions</c> whose enrollment manifest advertises
/// the capability, cross-team-excluded against the WORK's owning team, ordered
/// deterministically (capability specificity → oldest-online → endpointId);
/// the effect (assignment row + assign envelope + timeline + audit) commits in
/// one transaction, keyed by the intent's deterministic command ids, so a
/// crash-replay lands on the unique ledgers instead of double-assigning.
/// No candidate ⇒ the audit row records <c>no-candidate</c> vs
/// <c>no-capacity</c> (an advertised-slot check distinguishes them) and the
/// row requeues with backoff forever — never dead-letters, because the
/// endpoint may simply not have booted yet (scale-up reads that row).
/// The <see cref="IDecisionProvider"/> seam is advisory-only: it sees the
/// allowed set and its preference is adopted only when in-set, audited.
/// </summary>
public sealed class RoutingSink(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    IEnumerable<IDecisionProvider> providers,
    TimeProvider timeProvider) : ISink
{
    /// <inheritdoc />
    public string Channel => "routing";

    /// <summary>Descriptive (non-identity, non-execution) fields of a capability
    /// descriptor — the specificity the deterministic order ranks by: a worker
    /// that describes its capability more precisely is the better-matched one.</summary>
    private static readonly string[] DescriptorFields =
    [
        "inputSchema", "acceptedContexts", "outputKinds", "outputMediaTypes",
        "supportsSteering", "supportsCancellation", "supportsNudge",
    ];

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
    {
        var intent = ParseIntent(row.Envelope);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == intent.WorkId)
            .Select(w => new { w.WorkId, w.ProjectId, TeamId = w.Project!.TeamId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"routing intent for unknown work {intent.WorkId}");

        // Cross-team exclusion: the WORK's owning team is authoritative; the
        // intent's team field is the caller's view (advisory). A work without
        // a project routes team-agnostically.
        var workTeamSlug = work.TeamId is { } teamId
            ? await db.Teams.AsNoTracking().Where(t => t.TeamId == teamId)
                .Select(t => t.Slug).FirstOrDefaultAsync(ct)
            : null;

        // The DAG gate: an assign whose siblings haven't released
        // it requeues with the dispatcher's backoff (never dead-letters) —
        // a sibling's terminal/validated/approved event pulls it forward
        // under the wake law. Audit-first so the pending basis is truthful.
        var unmet = await DependencyGate.UnmetAsync(db, intent.DependsOnJson, ct);
        if (unmet.Count > 0)
        {
            var gatedAudit = await db.RoutingDecisions.FirstOrDefaultAsync(
                d => d.IntentCommandId == intent.IntentCommandId, ct);
            UpsertAudit(db, gatedAudit, intent, work.WorkId, allowedJson: "[]", chosen: null,
                "pending", "deps-unmet", providerRef: null, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new RoutePendingException("deps-unmet");
        }

        var (candidates, advertisedSlot) = await ComputeCandidatesAsync(db, intent, workTeamSlug, ct);

        var allowedJson = JsonSerializer.Serialize(candidates.Select(c => c.EndpointId).ToArray());
        var audit = await db.RoutingDecisions.FirstOrDefaultAsync(
            d => d.IntentCommandId == intent.IntentCommandId, ct);
        if (candidates.Count == 0)
        {
            var pendingBasis = advertisedSlot ? "no-capacity" : "no-candidate";
            UpsertAudit(db, audit, intent, work.WorkId, allowedJson, chosen: null, "pending", pendingBasis, providerRef: null, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            // No-route law: requeue with backoff, never dead-letter.
            throw new RoutePendingException(pendingBasis);
        }

        var (chosen, basis, providerRef) = await ChooseAsync(db, intent, candidates, workTeamSlug, ct);

        // Crash-replay idempotency: the effect row's command id is the
        // ledger. An already-present effect row ⇒ the prior pass committed
        // and died before the mark; re-record the decision, deliver nothing twice.
        var effectExists = await db.Outbox.AsNoTracking()
            .AnyAsync(o => o.CommandId == intent.EffectCommandId, ct);
        if (!effectExists)
        {
            EmitEffect(db, intent, work.TeamId, chosen, now);
        }

        UpsertAudit(db, audit, intent, work.WorkId, allowedJson, chosen, "routed", basis, providerRef, now);
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, work.WorkId.ToString(), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Allowed candidates (deterministic order) + whether the
    /// capability+team slot is advertised at all by some (unrevoked, unexpired)
    /// enrollment — live or not. That distinction is what the scale-up trigger
    /// reads: <c>no-capacity</c> = advertised somewhere but no live session
    /// (boot one); <c>no-candidate</c> = nobody in the fleet ever advertised it.
    /// Liveness = an unexpired session lease; the fleet team rides the
    /// manifest's ownerTeam (the capabilities POST validates it against
    /// the enrollment's team, so one source is enough).</summary>
    private async Task<(List<RoutingCandidate> Candidates, bool AdvertisedSlot)> ComputeCandidatesAsync(
        FacilitatorDbContext db, RoutingIntent intent, string? workTeamSlug, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var live = await db.LinkSessions.AsNoTracking()
            .Where(s => s.LeaseUntil > now)
            .Select(s => new { s.LogicalEndpointId, s.OpenedAt })
            .ToListAsync(ct);
        var openedBy = live.ToDictionary(s => s.LogicalEndpointId, s => s.OpenedAt);

        var enrollments = await db.LinkEnrollments.AsNoTracking()
            .Where(e => e.RevokedAt == null && e.ExpiresAt > now)
            .Select(e => new { e.EndpointId, e.CapabilityManifest })
            .ToListAsync(ct);

        var candidates = new List<RoutingCandidate>();
        var advertisedSlot = false;
        foreach (var r in enrollments)
        {
            var descriptor = MatchCapability(r.CapabilityManifest, intent.CapabilityId);
            if (descriptor is null)
            {
                continue;
            }

            var enrollmentTeam = TeamSlugOf(r.CapabilityManifest);
            if (workTeamSlug is not null && enrollmentTeam is not null
                && !string.Equals(workTeamSlug, enrollmentTeam, StringComparison.Ordinal))
            {
                continue; // cross-team exclusion (manifest ownerTeam)
            }

            advertisedSlot = true;
            if (openedBy.TryGetValue(r.EndpointId, out var openedAt))
            {
                candidates.Add(new RoutingCandidate(r.EndpointId, Specificity(descriptor), openedAt));
            }
        }

        // Deterministic order: specificity desc → oldest online → endpointId.
        return (candidates
            .OrderByDescending(c => c.Specificity)
            .ThenBy(c => c.OpenedAt)
            .ThenBy(c => c.EndpointId, StringComparer.Ordinal)
            .ToList(), advertisedSlot);
    }

    /// <summary>The matching descriptor entry (first, lowest version wins the
    /// identity), or null when the manifest does not advertise the capability.</summary>
    internal static JsonNode? MatchCapability(string? manifestJson, string capabilityId)
    {
        if (manifestJson is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var capabilities = JsonNode.Parse(manifestJson)?["capabilities"] as JsonArray;
            return capabilities?.OfType<JsonObject>()
                .Where(c => c["id"]?.GetValue<string>() == capabilityId)
                .OrderBy(c => c["version"]?.GetValue<int>() ?? int.MaxValue)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The manifest's ownerTeam (the enrollment's fleet team as
    /// advertised); null when absent/unparseable.</summary>
    internal static string? TeamSlugOf(string? manifestJson)
    {
        if (manifestJson is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(manifestJson)?["ownerTeam"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Specificity(JsonNode descriptor) =>
        DescriptorFields.Count(f => descriptor.AsObject().ContainsKey(f));

    /// <summary>Preference law: an intent preference (rule/global
    /// endpoint) is honored when in-set; otherwise the deterministic head
    /// stands. An <c>routing-defaults</c> provider is consulted over the
    /// allowed set only and an out-of-set answer falls back, audited;
    /// an unregistered provider (e.g. the reserved <c>"model"</c>) falls back
    /// with the <c>model-fallback</c> basis.</summary>
    private async Task<(string Chosen, string Basis, string? ProviderRef)> ChooseAsync(
        FacilitatorDbContext db, RoutingIntent intent, List<RoutingCandidate> candidates, string? workTeamSlug, CancellationToken ct)
    {
        var allowed = candidates.Select(c => c.EndpointId).ToHashSet(StringComparer.Ordinal);
        if (intent.PreferenceEndpointId is { Length: > 0 } preference)
        {
            return allowed.Contains(preference)
                ? (preference, "preference-satisfied", null)
                : (candidates[0].EndpointId, "preference-ignored", null);
        }

        var configured = await ConfiguredProviderAsync(db, ct);
        if (configured is not { Length: > 0 } name || name == "deterministic")
        {
            return (candidates[0].EndpointId, "deterministic-order", null);
        }

        var provider = providers.FirstOrDefault(p => p.Name == name);
        if (provider is null)
        {
            // Stored-accepted, no implementation registered (the reserved
            // "model" seam): deterministic stands, audited.
            return (candidates[0].EndpointId, "model-fallback", name);
        }

        var facts = new Dictionary<string, string>
        {
            ["capabilityId"] = intent.CapabilityId,
            ["workId"] = intent.WorkId.ToString(),
        };
        if (workTeamSlug is { Length: > 0 } team)
        {
            facts["team"] = team;
        }

        var outcome = await provider.EvaluateAsync(DecisionKind.Routing, facts, candidates, ct);
        if (outcome is not null && allowed.Contains(outcome.PreferredCandidate))
        {
            return (outcome.PreferredCandidate, "preference-satisfied", provider.Name);
        }

        return (candidates[0].EndpointId, "deterministic-order", provider.Name);
    }

    private static async Task<string?> ConfiguredProviderAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "routing-defaults")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload)?["provider"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The routing effect: assignment row (routed, generation 1) +
    /// the assign envelope outbox row + timeline <c>assignment.transition</c>,
    /// all in the decision's transaction.</summary>
    private void EmitEffect(
        FacilitatorDbContext db, RoutingIntent intent, Guid? workTeamId, string chosen, DateTime now)
    {
        // The DAG-visible identity: derived from the effect command id, so a
        // create that wrote sibling dependencies into `depends_on` before any
        // funnel ran joins onto exactly this assignment.
        var assignmentId = intent.DeterministicAssignmentId.ToString("N");
        db.Assignments.Add(new AssignmentEntity
        {
            AssignmentId = Guid.Parse(assignmentId),
            WorkId = intent.WorkId,
            TeamId = workTeamId,
            CapabilityId = intent.CapabilityId,
            Status = "routed",
            DependsOn = intent.DependsOnJson,
            CurrentExecutionGeneration = intent.ExecutionGeneration,
            ExpectedContextVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var extraInputs = ParseObject(intent.ExtraInputsJson);
        if (intent.SourceRefsJson is { Length: > 0 })
        {
            extraInputs ??= [];
            extraInputs["sourceRefs"] = JsonNode.Parse(intent.SourceRefsJson)!.DeepClone();
        }

        var envelope = ProviderEnvelopes.Assign(
            intent.WorkId, assignmentId, intent.Objective, intent.CapabilityId, intent.TargetId,
            now, extraInputs, intent.EffectCommandId, intent.ActorEvidenceRef,
            intent.ExecutionGeneration);
        // Durable-queue trace law, creation side: the carrier rides the row
        // BEFORE insert (validated first: the schema is carrier-free, the
        // dispatcher restamps link rows the same post-validation way), so
        // the byte the link plane delivers equals the stored row and the
        // dispatcher's restamp never races a wake-driven pump claim of
        // unstamped bytes.
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"routing effect envelope rejected: {verdict.ErrorClass}: {verdict.Message}");
        }

        var carrier = Activity.Current?.Id
            ?? $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";
        envelope["traceparent"] = carrier;
        json = envelope.ToJsonString();

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = $"link.{chosen}",
            AggregateType = "work",
            AggregateId = intent.WorkId,
            EventType = "org.facilitator.command.work.assign.v1",
            CommandId = intent.EffectCommandId,
            Envelope = json,
            WorkId = intent.WorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = intent.WorkId,
            WorkVersion = 1,
            Kind = "assignment.transition",
            Actor = "kernel:routing",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["assignmentId"] = assignmentId,
                ["endpointId"] = chosen,
                ["from"] = "proposed",
                ["to"] = "routed",
                ["capabilityId"] = intent.CapabilityId,
            }),
            CreatedAt = now,
        });
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (json is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void UpsertAudit(
        FacilitatorDbContext db,
        RoutingDecisionEntity? audit,
        RoutingIntent intent,
        Guid workId,
        string allowedJson,
        string? chosen,
        string status,
        string basis,
        string? providerRef,
        DateTime now)
    {
        if (audit is null)
        {
            db.RoutingDecisions.Add(new RoutingDecisionEntity
            {
                DecisionId = Guid.NewGuid(),
                WorkId = workId,
                IntentCommandId = intent.IntentCommandId,
                CapabilityId = intent.CapabilityId,
                TeamSlug = intent.TeamSlug,
                AllowedCandidates = allowedJson,
                ChosenEndpointId = chosen,
                Status = status,
                Basis = basis,
                ProviderRef = providerRef,
                LastRoutingAttemptAt = now,
                AttemptCount = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        audit.AllowedCandidates = allowedJson;
        audit.ChosenEndpointId = chosen;
        audit.Status = status;
        audit.Basis = basis;
        audit.ProviderRef = providerRef;
        audit.LastRoutingAttemptAt = now;
        audit.AttemptCount += 1;
        audit.UpdatedAt = now;
    }

    internal static RoutingIntent ParseIntent(JsonElement envelope)
    {
        var data = envelope.GetProperty("data");
        return new RoutingIntent(
            Guid.Parse(data.GetProperty("workId").GetString()!),
            data.GetProperty("capabilityId").GetString()!,
            data.TryGetProperty("team", out var team) ? team.GetString() : null,
            data.TryGetProperty("preferenceEndpointId", out var pref) ? pref.GetString() : null,
            data.GetProperty("targetId").GetString()!,
            data.GetProperty("commandIdBase").GetString()!,
            data.GetProperty("effectKey").GetString()!,
            data.GetProperty("objective").GetString()!,
            data.TryGetProperty("referenceJson", out var reference) ? reference.GetString() : null,
            data.TryGetProperty("extraInputsJson", out var extras) ? extras.GetString() : null,
            data.GetProperty("actorEvidenceRef").GetString()!,
            data.TryGetProperty("executionGeneration", out var gen) ? gen.GetInt32() : 1,
            data.TryGetProperty("dependsOnJson", out var deps) ? deps.GetString() : null);
    }
}
