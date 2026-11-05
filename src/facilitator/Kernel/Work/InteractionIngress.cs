using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>Team-level fan-out config for human input requests (the
/// <c>OFAC:InteractionDefaultEndpoint</c> key).</summary>
public sealed class InteractionOptions
{
    /// <summary>The config section carrying interaction knobs.</summary>
    public const string SectionName = "OFAC";

    /// <summary>Link endpoint id the kernel addresses a question at when the
    /// work has no originating-integration communication binding (arm b).</summary>
    public string? InteractionDefaultEndpoint { get; init; }
}

/// <summary>What an interaction arm hands back to the ingress that invoked it.</summary>
/// <param name="Problem">Typed problem to answer with; null = handled.</param>
/// <param name="Status">Outcome status wire value when handled.</param>
/// <param name="RequestId">The input request the arm acted on (echoed in bodies).</param>
/// <param name="EvidenceRef">Minted <c>kernel:</c> receipt for a REST respond (approval law D5).</param>
public sealed record InteractionArm(ProblemDetails? Problem, string Status, string? RequestId = null, string? EvidenceRef = null)
{
    /// <summary>True when the arm refused — the caller must NOT settle the inbox as processed.</summary>
    public bool Refused => Problem is not null;
}

/// <summary>Outcome of the shared respond core.</summary>
internal enum InteractionSettle
{
    Answered,
    NotFound,
    AlreadyAnswered,
    Fenced,
    NoPrincipal,
}

/// <summary>
/// The interactive leg: <c>event.assignment.input.required.v1</c> parks the
/// assignment row in its <c>input_required</c> side-state, records the question
/// on the timeline (kind <c>input.required</c>, correlation = requestId), and
/// fans the question out in the frozen order: (a) the work's originating
/// integration via its <c>communication_bindings</c> row, as a
/// <c>command.communication.reply.v1</c>; (b) the team's default endpoint
/// (<c>OFAC:InteractionDefaultEndpoint</c>); (c) an <see cref="IDecisionProvider"/>
/// pick among live communication-capable sessions, audited like the routing
/// funnel; (d) timeline-only + one <c>work.progress.v1</c> note. Every leg is an
/// outbox row routed by channel — never direct endpoint addressing.
/// The answer arrives on the REST surface (<see cref="InteractionEndpoints"/>)
/// or the link plane as <c>command.assignment.input.v1</c>; the respond core
/// transitions <c>input_required → running</c>, records <c>input.answered</c>,
/// and routes the answer envelope to the assignment's bound endpoint, deduped
/// by the command-ledger id <c>input:&lt;requestId&gt;</c>.
/// </summary>
public sealed class InteractionIngress(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    ProviderRegistry registry,
    IEnumerable<IDecisionProvider> providers,
    InteractionOptions options,
    TimeProvider timeProvider)
{
    /// <summary>Envelope type of the input-required event.</summary>
    public const string InputRequiredType = "org.facilitator.event.assignment.input.required.v1";

    /// <summary>Envelope type of the answer command.</summary>
    public const string InputCommandType = "org.facilitator.command.assignment.input.v1";

    /// <summary>Timeline kind recording a pending question.</summary>
    public const string KindRequired = "input.required";

    /// <summary>Timeline kind recording an accepted answer.</summary>
    public const string KindAnswered = "input.answered";

    /// <summary>The command-ledger dedupe id for one request's answer.</summary>
    public static string CommandIdFor(string requestId) => $"input:{requestId}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Event-class arm (both planes): park + timeline + fan-out.</summary>
    public async Task<InteractionArm> HandleEventAsync(
        JsonElement root, string surfaceActor, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var workText = Str(data, "workId") ?? string.Empty;
        var requestId = Str(data, "requestId") ?? string.Empty;
        var assignmentText = Str(data, "assignmentId");
        var question = Str(data, "question") ?? string.Empty;
        var generation = data.TryGetProperty("executionGeneration", out var g) && g.TryGetInt32(out var gv) ? gv : 1;

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var workId = Guid.TryParse(workText, out var parsed) ? parsed : Guid.Empty;
        var version = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return new InteractionArm(ProblemErrors.WorkNotFound(workId), "failed", requestId);
        }

        var assignmentId = Guid.TryParse(assignmentText, out var aid) ? aid : (Guid?)null;
        var row = assignmentId is null
            ? null
            : await db.Assignments.AsNoTracking()
                .Where(a => a.AssignmentId == assignmentId)
                .Select(a => new { a.Status, a.CurrentExecutionGeneration })
                .FirstOrDefaultAsync(ct);
        if (row is not null && row.CurrentExecutionGeneration != generation)
        {
            return new InteractionArm(ProblemErrors.StaleGeneration(generation, row.CurrentExecutionGeneration), "failed", requestId);
        }

        var pending = await db.WorkEvents.AnyAsync(
            e => e.WorkId == workId && e.Kind == KindRequired && e.CorrelationId == requestId, ct);
        if (pending)
        {
            await tx.RollbackAsync(ct);
            return new InteractionArm(null, "duplicate", requestId);
        }

        // Park: walk routed/accepted to running first (legal edges), then the
        // guarded running→input_required edge; a terminal row stays terminal.
        if (row is not null && assignmentId is not null)
        {
            var parkId = assignmentId.Value;
            await ExecutionLedger.WalkToRunningAsync(db, parkId, now, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.assignments SET status = 'input_required', updated_at = {now}
                  WHERE assignment_id = {parkId} AND status = 'running'
                 """, ct);
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = KindRequired,
            Actor = surfaceActor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["question"] = Truncate(question, 2048),
                ["assignmentId"] = assignmentText,
                ["executionId"] = Str(data, "executionId"),
                ["executionGeneration"] = generation,
                ["choices"] = data.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToArray()
                    : null,
                ["contextRef"] = Str(data, "contextRef"),
                ["deadline"] = Str(data, "deadline"),
            }, Json),
            CorrelationId = requestId,
            CreatedAt = now,
        });

        var choices = data.TryGetProperty("choices", out var ce) && ce.ValueKind == JsonValueKind.Array
            ? ce.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : [];
        await FanOutAsync(db, workId, requestId, question, choices, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return new InteractionArm(null, "duplicate", requestId);
        }

        await PostgresNotifications.NotifyOutboxAsync(db, workId.ToString(), ct);
        await tx.CommitAsync(ct);
        return new InteractionArm(null, "parked", requestId);
    }

    /// <summary>Command-class arm (link + CE planes): the answer to a pending
    /// request, arriving as <c>command.assignment.input.v1</c> from a
    /// communication adapter. Same settle/404/409 law as the REST respond.</summary>
    public async Task<InteractionArm> HandleCommandRespondAsync(
        JsonElement root, string fallbackActor, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var requestId = Str(data, "requestId") ?? string.Empty;
        var answer = Str(data, "answer") ?? string.Empty;
        var evidenceRef = Str(data, "actorEvidenceRef");
        var (outcome, _) = await SettleAsync(
            workScope: null, requestId, answer, actor: fallbackActor,
            evidenceRef, mintEvidence: false, ct);
        return ToArm(outcome, requestId);
    }

    /// <summary>REST respond surface (human plane): identity resolution +
    /// <c>kernel:</c> minted receipt per the approval respond law D5.</summary>
    public async Task<InteractionArm> HandleRespondAsync(
        Guid workId, string requestId, string answer, string actor, IReadOnlyList<string> groups, CancellationToken ct)
    {
        var (outcome, evidenceRef) = await SettleAsync(
            workId, requestId, answer, actor, evidenceRef: null,
            mintEvidence: true, ct, groups);
        var arm = ToArm(outcome, requestId);
        return outcome == InteractionSettle.Answered
            ? arm with { EvidenceRef = evidenceRef }
            : arm;
    }

    /// <summary>The shared respond core on a fresh context + transaction; the
    /// second tuple slot is the evidence ref the answer rode (minted
    /// <c>kernel:</c> receipt on the REST surface, the envelope's ref
    /// otherwise).</summary>
    internal async Task<(InteractionSettle Outcome, string? EvidenceRef)> SettleAsync(
        Guid? workScope, string requestId, string answer, string actor,
        string? evidenceRef, bool mintEvidence, CancellationToken ct,
        IReadOnlyList<string>? groups = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (outcome, usedRef) = await AppendSettleAsync(
            db, envelopeValidator, workScope, requestId, answer, actor, evidenceRef, mintEvidence, groups, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return (InteractionSettle.AlreadyAnswered, null);
        }

        if (outcome != InteractionSettle.Answered)
        {
            await tx.RollbackAsync(ct);
            return (outcome, null);
        }

        await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        await tx.CommitAsync(ct);
        return (InteractionSettle.Answered, usedRef);
    }

    private InteractionArm ToArm(InteractionSettle outcome, string requestId) => outcome switch
    {
        InteractionSettle.Answered => new InteractionArm(null, "answered", requestId),
        InteractionSettle.NotFound => new InteractionArm(
            ProblemErrors.Create("interaction-not-found", "Interaction not found",
                StatusCodes.Status404NotFound, $"no pending input request '{requestId}'"), "failed", requestId),
        InteractionSettle.AlreadyAnswered => new InteractionArm(
            ProblemErrors.Create("interaction-already-answered", "Interaction already answered",
                StatusCodes.Status409Conflict, $"input request '{requestId}' was already answered (command {CommandIdFor(requestId)})"),
            "failed", requestId),
        InteractionSettle.Fenced => new InteractionArm(
            ProblemErrors.Create("interaction-fence-mismatch", "Interaction fence mismatch",
                StatusCodes.Status409Conflict, $"input request '{requestId}' belongs to a superseded execution generation"),
            "failed", requestId),
        _ => new InteractionArm(ProblemErrors.ApprovalActorUnknown(), "failed", requestId),
    };
    /// <summary>The respond rows on an OPEN context (the caller's
    /// transaction): pending lookup + answered check + fence + guarded
    /// transition + timeline + answer envelope. Tracked only — the caller
    /// saves (and maps a unique-command clash to AlreadyAnswered).</summary>
    internal static async Task<(InteractionSettle Outcome, string? EvidenceRef)> AppendSettleAsync(
        FacilitatorDbContext db, EnvelopeValidator validator, Guid? workScope, string requestId, string answer, string actor,
        string? evidenceRef, bool mintEvidence, IReadOnlyList<string>? groups, CancellationToken ct)
    {
        var pending = await db.WorkEvents.AsNoTracking()
            .Where(e => e.Kind == KindRequired && e.CorrelationId == requestId
                && (workScope == null || e.WorkId == workScope))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (pending is null)
        {
            return (InteractionSettle.NotFound, null);
        }

        if (await db.WorkEvents.AnyAsync(
                e => e.Kind == KindAnswered && e.CorrelationId == requestId, ct))
        {
            return (InteractionSettle.AlreadyAnswered, null);
        }

        var detail = JsonDocument.Parse(pending.Detail).RootElement;
        var assignmentId = Guid.TryParse(Str(detail, "assignmentId"), out var aid) ? aid : (Guid?)null;
        var generation = detail.TryGetProperty("executionGeneration", out var g) && g.TryGetInt32(out var gv) ? gv : 0;
        var current = assignmentId is null
            ? null
            : await db.Assignments.AsNoTracking()
                .Where(a => a.AssignmentId == assignmentId)
                .Select(a => (int?)a.CurrentExecutionGeneration)
                .FirstOrDefaultAsync(ct);
        if (current is not null && generation != 0 && current.Value != generation)
        {
            return (InteractionSettle.Fenced, null);
        }

        var now = DateTime.UtcNow;
        if (assignmentId is not null)
        {
            var unparkId = assignmentId.Value;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.assignments SET status = 'running', updated_at = {now}
                  WHERE assignment_id = {unparkId} AND status = 'input_required'
                 """, ct);
        }

        var evidenceId = Guid.NewGuid().ToString("N");
        var minted = mintEvidence || evidenceRef is null;
        if (minted)
        {
            db.Evidence.Add(new EvidenceEntity
            {
                EvidenceId = evidenceId,
                Subject = actor,
                Claims = JsonSerializer.Serialize(new Dictionary<string, object?> { ["groups"] = groups ?? [] }, Json),
                IssuedAt = now,
                ExpiresAt = now.AddDays(1),
                ConsumedAt = now,
            });
        }

        var usedRef = minted ? $"kernel:{evidenceId}" : evidenceRef!;
        var version = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == pending.WorkId).Select(w => w.Version).FirstOrDefaultAsync(ct);
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = pending.WorkId,
            WorkVersion = version,
            Kind = KindAnswered,
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["answer"] = Truncate(answer, 512),
                ["actor"] = actor,
                ["evidenceRef"] = usedRef,
            }, Json),
            CorrelationId = requestId,
            CreatedAt = now,
        });

        // The answer envelope rides to the assignment's bound receiving
        // endpoint (the Gateway); an unbound assignment settles timeline-only
        // with a progress note (the fan-out never assumes a bound run).
        var owner = assignmentId is null
            ? null
            : await ExecutionLedger.OwningEndpointAsync(db, pending.WorkId, assignmentId.Value, ct);
        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = InputCommandType,
            ["subject"] = $"work/{pending.WorkId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.assignment.input.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = CommandIdFor(requestId),
                ["requestId"] = requestId,
                ["answer"] = Truncate(answer, 16384),
                ["actorEvidenceRef"] = usedRef,
            },
        };
        AppendOutboxRow(
            db, validator, envelope, owner is null ? KernelLogChannel : "link." + owner,
            CommandIdFor(requestId), pending.WorkId, now);
        return (InteractionSettle.Answered, usedRef);
    }

    /// <summary>Provider-plane auto-respond (in the caller's transaction): a
    /// message on a conversation bound to a work with a pending question
    /// answers the OLDEST pending request instead of creating input.</summary>
    internal static async Task<string?> TryAutoRespondAsync(
        FacilitatorDbContext db, EnvelopeValidator validator, Guid workId, string answer, string provider,
        string deliveryId, string actor, CancellationToken ct)
    {
        var required = await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == workId && e.Kind == KindRequired)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.CorrelationId)
            .ToListAsync(ct);
        if (required.Count == 0)
        {
            return null;
        }

        var answered = (await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == workId && e.Kind == KindAnswered)
            .Select(e => e.CorrelationId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var oldest = required.FirstOrDefault(id => id is not null && !answered.Contains(id));
        if (oldest is null)
        {
            return null;
        }

        var (outcome, _) = await AppendSettleAsync(
            db, validator, workId, oldest, answer, actor,
            $"provider:{provider}:{deliveryId}", mintEvidence: false, groups: null, ct);
        return outcome == InteractionSettle.Answered ? oldest : null;
    }

    // ---- fan-out (frozen order a → b → c → d) --------------------------------

    private async Task FanOutAsync(
        FacilitatorDbContext db, Guid workId, string requestId, string question,
        string[] choices, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var text = Truncate(Render(question, choices, requestId), 8192);
        var fanoutId = $"input-fanout:{requestId}";

        // (a) The work's originating integration: the binding ResultIngress
        // writes, addressed like its connector commands — provider endpoint.
        var binding = await db.CommunicationBindings.AsNoTracking()
            .Where(b => b.WorkId == workId)
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (binding is not null && registry.Find(binding.Provider) is { EndpointId: { Length: > 0 } providerEndpoint })
        {
            var target = new ConversationTarget(
                binding.Provider, null, binding.Channel, binding.RootMessageId,
                null, requestId, "thread", null);
            var envelope = ProviderEventStore.CommunicationEnvelope(
                "org.facilitator.command.communication.reply.v1", "command.communication.reply.v1.json",
                target, $"work/{workId}", now, CommandIdFor(requestId),
                new JsonObject { ["text"] = text });
            AppendOutboxRow(db, envelopeValidator, envelope, "link." + providerEndpoint, fanoutId, workId, now);
            return;
        }

        // (b) The team's declared default endpoint (link plane, like dispatch rows).
        if (options.InteractionDefaultEndpoint is { Length: > 0 } defaultEndpoint)
        {
            AppendOutboxRow(db, envelopeValidator, QuestionEnvelope(workId, text, defaultEndpoint, requestId, now),
                "link." + defaultEndpoint, fanoutId, workId, now);
            return;
        }

        // (c) A registered decision seam picks among live communication-capable
        // sessions; only an in-set answer is adopted, audited like the funnel.
        var providerList = providers.ToList();
        if (providerList.Count > 0)
        {
            var candidates = await CommunicationCandidatesAsync(db, ct);
            if (candidates.Count > 0)
            {
                var allowed = candidates.Select(c => c.EndpointId).ToHashSet(StringComparer.Ordinal);
                var chosen = candidates[0].EndpointId;
                var basis = "deterministic-order";
                string? providerRef = null;
                var facts = new Dictionary<string, string>
                {
                    ["requestId"] = requestId,
                    ["workId"] = workId.ToString(),
                    ["question"] = Truncate(question, 512),
                };
                foreach (var provider in providerList)
                {
                    providerRef = provider.Name;
                    var outcome = await provider.EvaluateAsync(
                        DecisionKind.Communication, facts, candidates, ct);
                    if (outcome is not null && allowed.Contains(outcome.PreferredCandidate))
                    {
                        chosen = outcome.PreferredCandidate;
                        basis = "preference-satisfied";
                        break;
                    }
                }

                // A consulted provider that expressed no in-set preference
                // declined to steer — the funnel's own audit vocabulary.
                if (basis == "deterministic-order" && providerRef is { Length: > 0 } consulted && consulted != "deterministic")
                {
                    basis = "model-fallback";
                }

                var endpoint = registry.All()
                    .FirstOrDefault(o => string.Equals(o.EndpointId, chosen, StringComparison.Ordinal));
                var targetProvider = endpoint?.Provider ?? "kernel";
                var target = new ConversationTarget(
                    targetProvider, null, requestId, null, null, requestId, "channel", null);
                var envelope = ProviderEventStore.CommunicationEnvelope(
                    "org.facilitator.command.communication.reply.v1", "command.communication.reply.v1.json",
                    target, $"work/{workId}", now, CommandIdFor(requestId),
                    new JsonObject { ["text"] = text });
                AppendOutboxRow(db, envelopeValidator, envelope, "link." + chosen, fanoutId, workId, now);
                db.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    DecisionId = Guid.NewGuid(),
                    WorkId = workId,
                    IntentCommandId = fanoutId,
                    CapabilityId = "communication.reply.v1",
                    AllowedCandidates = JsonSerializer.Serialize(
                        candidates.Select(c => c.EndpointId).ToArray(), Json),
                    ChosenEndpointId = chosen,
                    Status = "routed",
                    Basis = basis,
                    ProviderRef = providerRef,
                    LastRoutingAttemptAt = now,
                    AttemptCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                return;
            }
        }

        // (d) Timeline-only + one progress note.
        AppendOutboxRow(db, envelopeValidator, EnvelopeFactory.WorkProgress(
            workId, Truncate("waiting for input: " + question, 4000), null),
            KernelLogChannel, $"input-progress:{requestId}", workId, now);
    }

    /// <summary>Live link sessions whose enrollment manifest advertises a
    /// communication command (the reply/react/ack family) — deterministic
    /// order: oldest online, then endpointId (no specificity concept here).</summary>
    private async Task<List<RoutingCandidate>> CommunicationCandidatesAsync(
        FacilitatorDbContext db, CancellationToken ct)
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
        foreach (var enrollment in enrollments)
        {
            if (CommunicationCapable(enrollment.CapabilityManifest)
                && openedBy.TryGetValue(enrollment.EndpointId, out var openedAt))
            {
                candidates.Add(new RoutingCandidate(enrollment.EndpointId, 0, openedAt));
            }
        }

        return candidates
            .OrderBy(c => c.OpenedAt)
            .ThenBy(c => c.EndpointId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when the manifest advertises any communication reply-family
    /// capability (descriptor id containing "communication").</summary>
    internal static bool CommunicationCapable(string? manifestJson)
    {
        if (manifestJson is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            return (JsonNode.Parse(manifestJson)?["capabilities"] as JsonArray)?
                .OfType<JsonObject>()
                .Any(c => c["id"]?.GetValue<string>()?.Contains("communication", StringComparison.Ordinal) == true) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonObject QuestionEnvelope(
        Guid workId, string text, string endpoint, string requestId, DateTime now)
    {
        _ = endpoint;
        return new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = "org.facilitator.command.communication.reply.v1",
            ["subject"] = $"work/{workId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.communication.reply.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = CommandIdFor(requestId),
                ["target"] = new JsonObject
                {
                    ["provider"] = "kernel",
                    ["channelOrResourceId"] = requestId,
                    ["conversationKind"] = "channel",
                },
                ["text"] = text,
                ["idempotencyKey"] = CommandIdFor(requestId),
            },
        };
    }

    private static void AppendOutboxRow(
        FacilitatorDbContext db, EnvelopeValidator validator, JsonObject envelope, string recipient,
        string commandId, Guid workId, DateTime now)
    {
        var json = envelope.ToJsonString();
        var verdict = validator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = recipient,
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            CommandId = commandId,
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    /// <summary>The rendered question: choices numbered, reply-trailer naming
    /// the request id.</summary>
    internal static string Render(string question, string[] choices, string requestId)
    {
        var text = question;
        if (choices.Length > 0)
        {
            text += "\n\n" + string.Join("\n", choices.Select((c, i) => $"{i + 1}) {c}"));
        }

        return text + $"\n\nReply in this thread to answer (request {requestId})";
    }

    private static readonly string KernelLogChannel = "kernel.log";

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

}
