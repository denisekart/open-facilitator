using System.Text.Json;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// EF Core implementation of the coordination seam. Each mutation opens
/// one transaction, takes <c>pg_advisory_xact_lock(hashtext(work_id))</c> so
/// per-Work decisions serialize, applies a version-checked UPDATE,
/// appends the timeline + outbox rows, and pokes <c>NOTIFY facilitator_outbox</c>
/// (hint only) — all before commit.
/// </summary>
public sealed class EfCoordinationStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider,
    Facilitator.Kernel.Integrations.ProviderRegistry? providers = null,
    AiPolicyStore? aiPolicies = null,
    CommunicationDecision? communication = null) : ICoordinationStore
{
    private const string KernelLogChannel = "kernel.log";

    /// <inheritdoc />
    public async Task<WorkView> CreateWorkAsync(string objective, Guid? projectId, string actor, CancellationToken ct, IReadOnlyList<RoutingIntentSeed>? assignments = null, WorkOrigin? origin = null)
    {
        var workId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await WorkLockAsync(db, workId, ct);

        db.Works.Add(new WorkEntity
        {
            WorkId = workId,
            ProjectId = projectId,
            Objective = objective,
            State = WorkState.Open.Wire(),
            Version = 1,
            TeamId = origin?.TeamId ?? ProjectTeamId(db, projectId),
            OwnerActor = origin?.OwnerActor,
            Origin = origin?.WireType,
            ActiveWhere = origin?.ActiveWhere,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = 1,
            Kind = "created",
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["objective"] = objective }),
            CreatedAt = now,
        });

        AppendWorkProgress(db, workId, "work created", causationId: null, createdAt: now);

        // The REST twin of the provider arms: every requested assignment
        // rides as a routing intent in this same transaction — the funnel
        // resolves the endpoint. Team = the project's team (cross-team
        // exclusion is the funnel's law); identity is deterministic per
        // (work, index) so a replayed create collides on the ledger.
        if (assignments is { Count: > 0 })
        {
            // The create-time DAG law: validate the batch and
            // resolve sibling index references to the deterministic sibling
            // identities BEFORE the first funnel run, so depends_on joins
            // exactly. A violation aborts the create (422 at the ingress).
            var depsPerSeed = DagLaw.ResolveCreateBatch(workId, assignments);
            var teamSlug = projectId is { } pid
                ? await db.Projects.AsNoTracking()
                    .Where(p => p.ProjectId == pid)
                    .Select(p => p.Team!.Slug).FirstOrDefaultAsync(ct)
                : null;
            for (var i = 0; i < assignments.Count; i++)
            {
                var seed = assignments[i];
                var intent = new RoutingIntent(
                    workId,
                    seed.CapabilityId,
                    teamSlug,
                    seed.PreferenceEndpointId,
                    TargetId: seed.TargetId is { Length: > 0 } target ? target : "kernel",
                    CommandIdBase: "work-assign",
                    EffectKey: RoutingIntent.CreateEffectKey(workId, i),
                    Objective: seed.Objective is { Length: > 0 } obj ? obj : objective,
                    SourceRefsJson: null,
                    ExtraInputsJson: seed.Inputs is { ValueKind: JsonValueKind.Object } inputs ? inputs.GetRawText() : null,
                    ActorEvidenceRef: seed.ActorEvidenceRef is { Length: > 0 } evidence
                        ? evidence : $"kernel:rest-ingress:{workId}",
                    DependsOnJson: depsPerSeed[i]);
                db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
            }
        }
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);

        var created = await LoadAsync(db, workId, ct);
        return created ?? throw new InvalidOperationException($"work {workId} vanished right after creation");
    }

    /// <inheritdoc />
    public async Task<bool> RedispatchAsync(Guid workId, RoutingIntentSeed seed, string? idempotencyKey, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await WorkLockAsync(db, workId, ct);
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new { w.Objective, TeamSlug = w.ProjectId == null ? null : w.Project!.Team!.Slug, w.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // An explicit re-dispatch is the next execution generation of
        // the work's assignment line (max prior attempt + 1; 1 for a bare work).
        var priorGeneration = await db.ExecutionAttempts.AsNoTracking()
            .Where(a => a.Assignment!.WorkId == workId)
            .Select(a => (int?)a.Generation)
            .MaxAsync(ct) ?? 0;
        var intent = new RoutingIntent(
            workId,
            seed.CapabilityId,
            work.TeamSlug,
            seed.PreferenceEndpointId,
            TargetId: seed.TargetId is { Length: > 0 } target ? target : "kernel",
            CommandIdBase: "work-redispatch",
            EffectKey: idempotencyKey is { Length: > 0 } key ? key : Guid.NewGuid().ToString("N"),
            Objective: seed.Objective is { Length: > 0 } obj ? obj : work.Objective,
            SourceRefsJson: null,
            ExtraInputsJson: seed.Inputs is { ValueKind: JsonValueKind.Object } inputs ? inputs.GetRawText() : null,
            ActorEvidenceRef: seed.ActorEvidenceRef is { Length: > 0 } evidence
                ? evidence : $"kernel:rest-ingress:{workId}",
            ExecutionGeneration: priorGeneration + 1);
        db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
        AppendWorkProgress(db, workId, "re-dispatch requested", causationId: null, createdAt: now);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<TransitionResult> ApplyTransitionAsync(Guid workId, WorkState to, long expectedVersion, string actor, string? reason, CancellationToken ct, TransitionOptions? options = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await WorkLockAsync(db, workId, ct);

        var current = await LoadAsync(db, workId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return new TransitionResult.NotFound();
        }

        if (!StateGuard.TryParseWork(current.State, out var from) || !StateGuard.CanTransition(from, to))
        {
            await tx.RollbackAsync(ct);
            return new TransitionResult.Illegal($"{current.State}→{to.Wire()}");
        }

        var basis = options?.Basis ?? "complete";
        if (to == WorkState.Completed && basis == "partially-complete" && options?.SuccessorWorkId is null)
        {
            // Partial success without a successor has no evidence trail: the
            // remainder of the ask would silently vanish.
            await tx.RollbackAsync(ct);
            return new TransitionResult.SuccessorRequired();
        }

        if (to == WorkState.Completed && !await HasClosureEvidenceAsync(db, workId, options, reason, ct))
        {
            // A completion with nothing to point at is a claim, not a record:
            // refuse until the completer names a basis (close_reason).
            await tx.RollbackAsync(ct);
            return new TransitionResult.Unevidenced();
        }

        var bumped = await BumpVersionAsync(db, workId, to.Wire(), expectedVersion, ct);
        if (bumped == 0)
        {
            var latest = await LoadAsync(db, workId, ct);
            await tx.RollbackAsync(ct);
            return latest is null
                ? new TransitionResult.NotFound()
                : new TransitionResult.Conflict(latest.Version);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var transitionEventId = Guid.NewGuid();
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = transitionEventId,
            WorkId = workId,
            WorkVersion = expectedVersion + 1,
            Kind = "state.transition",
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["from"] = current.State,
                ["to"] = to.Wire(),
                ["reason"] = reason,
                ["basis"] = options is null || basis == "complete" ? null : basis,
            }),
            CreatedAt = now,
        });

        if (options?.SuccessorWorkId is { } successorId)
        {
            // The successor link is a first-class row, and the follow-up work
            // must exist: a dangling successor id would be the evidence the
            // closure is missing.
            await AddWorkLinkAsync(db, workId, successorId, "successor", options.Note, now, ct);
        }

        if (to == WorkState.Completed)
        {
            // The closure basis lands in its own column, never only in event
            // detail: an artifact, a manifest, a work-link, or this reason is
            // what makes a completion a record instead of a claim.
            var closeReason = options?.CloseReason is { Length: > 0 } cr
                ? cr
                : reason is { Length: > 0 } ? reason : "completed";
            await db.Works.Where(w => w.WorkId == workId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.CloseReason, closeReason), ct);
        }

        AppendWorkProgress(db, workId, $"work {current.State}→{to.Wire()}", causationId: null, createdAt: now);
        await AppendProviderCompletionReplyAsync(db, workId, to, current.State, transitionEventId, now, ct);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);

        return new TransitionResult.Ok((await LoadAsync(db, workId, ct))!);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkLinkRow>> LinksAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.WorkLinks.AsNoTracking()
            .Where(l => l.WorkId == workId)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new WorkLinkRow(l.LinkId, l.WorkId, l.RefWorkId, l.Kind, l.Note, l.CreatedAt))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<MergeResult> MergeAsync(Guid workId, Guid intoWorkId, string? reason, string actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WorkLockAsync(db, workId, ct);

        var loser = await LoadAsync(db, workId, ct);
        var survivor = await LoadAsync(db, intoWorkId, ct);
        if (loser is null || survivor is null)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult.NotFound();
        }

        if (workId == intoWorkId || loser.MergedInto == intoWorkId)
        {
            await tx.RollbackAsync(ct);
            return new MergeResult.Loop();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.WorkLinks.AddAsync(new WorkLinkEntity
        {
            LinkId = Guid.NewGuid(),
            WorkId = workId,
            RefWorkId = intoWorkId,
            Kind = "merged-duplicate",
            Note = reason,
            CreatedAt = now,
        }, ct);
        await db.Works.Where(w => w.WorkId == workId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.MergedInto, intoWorkId)
                .SetProperty(w => w.UpdatedAt, now), ct);
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = loser.Version,
            Kind = "work.linked",
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["refWorkId"] = intoWorkId.ToString(),
                ["kind"] = "merged-duplicate",
                ["note"] = reason,
            }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);
        return new MergeResult.Merged((await LoadAsync(db, intoWorkId, ct))!);
    }

    /// <inheritdoc />
    public async Task SetCostAsync(Guid workId, string costJson, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Works.Where(w => w.WorkId == workId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Cost, costJson)
                .SetProperty(w => w.UpdatedAt, timeProvider.GetUtcNow().UtcDateTime), ct);
    }

    /// <inheritdoc />
    public async Task<SteerResult> ApplySteerAsync(Guid workId, string directive, string commandId, long expectedVersion, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await WorkLockAsync(db, workId, ct);

        var current = await LoadAsync(db, workId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return new SteerResult.NotFound();
        }

        if (!StateGuard.TryParseWork(current.State, out var state) || StateGuard.IsTerminal(state))
        {
            await tx.RollbackAsync(ct);
            return new SteerResult.Terminal();
        }

        // Business-command dedupe: a steer's outbox row carries the command id
        // (unique), so an already-processed command id short-circuits inside
        // the advisory lock.
        if (await db.Outbox.AsNoTracking().AnyAsync(o => o.CommandId == commandId, ct))
        {
            await tx.RollbackAsync(ct);
            return new SteerResult.Duplicate();
        }

        var bumped = await BumpVersionAsync(db, workId, current.State, expectedVersion, ct);
        if (bumped == 0)
        {
            var latest = await LoadAsync(db, workId, ct);
            await tx.RollbackAsync(ct);
            return latest is null
                ? new SteerResult.NotFound()
                : new SteerResult.Conflict(latest.Version);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = expectedVersion + 1,
            Kind = "steer",
            Actor = "command",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["directive"] = directive,
                ["commandId"] = commandId,
            }),
            CorrelationId = commandId,
            CreatedAt = now,
        });

        var outbox = AppendWorkProgress(db, workId, directive, causationId: commandId, createdAt: now);
        outbox.CommandId = commandId;
        await AppendSteerDeliveryAsync(db, workId, directive, commandId, expectedVersion + 1, now, ct);

        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);

        return new SteerResult.Ok((await LoadAsync(db, workId, ct))!);
    }

    /// <inheritdoc />
    public async Task<WorkView?> GetAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, workId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkEventRow>> TimelineAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // Bound to the newest 1000 rows, then served chronologically.
        var rows = await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == workId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId)
            .Take(1000)
            .Select(e => new WorkEventRow(e.EventId, e.WorkId, e.WorkVersion, e.Kind, e.Actor, e.Detail, e.CorrelationId, e.CreatedAt))
            .ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    private const string WorkAssignType = "org.facilitator.command.work.assign.v1";

    private static readonly string[] AttemptTerminal = ["stopped", "failed", "lost"];

    /// <summary>A steer reaches the owning gateway. The
    /// outbox row that CARRIED the assign is the assignment→endpoint ledger
    /// (<c>assignments</c>/<c>link_deliveries</c> pin no endpoint — the
    /// outbox recipient survives as <c>link.&lt;endpoint&gt;</c>), so this
    /// arm reads the latest assign envelope back by its
    /// <c>work-assign*</c>/<c>work-redispatch</c> command id. A live
    /// execution (the work's latest execution attempt not yet terminal) ⇒
    /// an <c>org.facilitator.command.work.steer.v1</c> row to
    /// <c>link.&lt;endpoint&gt;</c>: the gateway's <c>onInteraction</c>
    /// delivers it to the bound child or honestly rejects
    /// (<c>no-binding</c>/<c>child-disconnected</c>) — a rejected steer is
    /// a normal result, the timeline row + progress note stand either way.
    /// No live execution ⇒ the restart-with-context leg: a fresh routing
    /// intent re-dispatches through the funnel (no arm addresses an endpoint
    /// directly) reusing the ORIGINAL envelope's capability/target/inputs
    /// (the worker re-hydrates from the branch + PR lineage); the original
    /// recipient rides as the intent's endpoint preference. The effect keeps
    /// the <c>work-redispatch:&lt;steerCommandId&gt;</c> command id, so the
    /// envelope read-back ledger survives the routing cutover and replay is
    /// idempotent on the unique command id. No assign row (a REST-created
    /// Work) ⇒ timeline record only, today's behavior.</summary>
    private async Task AppendSteerDeliveryAsync(
        FacilitatorDbContext db, Guid workId, string directive, string commandId, long contextVersion, DateTime now, CancellationToken ct)
    {
        var original = await db.Outbox.AsNoTracking()
            .Where(o => o.WorkId == workId && o.EventType == WorkAssignType
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (original is null)
        {
            return;
        }

        using var doc = JsonDocument.Parse(original.Envelope);
        var data = doc.RootElement.GetProperty("data");
        var latest = await db.ExecutionAttempts.AsNoTracking()
            .Where(a => a.Assignment!.WorkId == workId)
            .OrderByDescending(a => a.StartedAt).ThenByDescending(a => a.Generation)
            .FirstOrDefaultAsync(ct);
        if (latest is not null && !AttemptTerminal.Contains(latest.Status))
        {
            // A live steer does NOT bump the generation — it rides the
            // live attempt's generation, so the worker's echoed terminal
            // events still fence-match the assignment.
            var steer = Facilitator.Kernel.Integrations.ProviderEnvelopes.Steer(
                workId,
                data.GetProperty("assignmentId").GetString() ?? latest.AssignmentId.ToString("N"),
                data.GetProperty("targetId").GetString() ?? "kernel",
                directive,
                contextVersion,
                now,
                $"work-steer:{commandId}",
                executionGeneration: latest.Generation);
            AppendDeliveryRow(db, steer, original.RecipientEndpointId, $"work-steer:{commandId}", workId, now);
            return;
        }

        // Re-dispatch: a routing intent for the funnel. Identity is the steer
        // command id (effect `<base>:<steerId>` preserves the envelope
        // read-back ledger across the routing cutover); the envelope rebuilds
        // from the original's data. The re-dispatched run has no steer
        // channel of its own yet (the review leg is the FIRST thing it will
        // see), so the directive rides its inputs: `steers` accumulates one
        // entry per re-dispatch leg; the harness folds them into the change +
        // the "steer incorporated" commentRequest (steering law).
        var originalData = System.Text.Json.Nodes.JsonNode.Parse(original.Envelope)!.AsObject()["data"]!.AsObject();
        var originalInputs = originalData["inputs"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var objective = originalInputs["objective"]?.GetValue<string>() ?? string.Empty;
        var extraInputs = originalInputs.DeepClone().AsObject();
        extraInputs.Remove("objective", out _);
        extraInputs.Remove("sourceRefs", out _);
        var steers = extraInputs["steers"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        steers.Add(directive.Length <= 2048 ? directive : directive[..2048]);
        extraInputs["steers"] = steers;
        // The base is `work-redispatch` — the 24 ledger byte-for-byte
        // (effect = `work-redispatch:<steerId>`); the steer arm's
        // `work-assign*`/`work-redispatch` read-back keeps matching.
        const string baseCommandId = "work-redispatch";
        var workTeamSlug = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => w.ProjectId == null ? null : w.Project!.Team!.Slug).FirstOrDefaultAsync(ct);
        var redispatchIntent = new RoutingIntent(
            workId,
            originalData["capabilityId"]!.GetValue<string>(),
            workTeamSlug,
            PreferenceEndpointId: original.RecipientEndpointId.StartsWith("link.", StringComparison.Ordinal)
                ? original.RecipientEndpointId["link.".Length..] : original.RecipientEndpointId,
            TargetId: originalData["targetId"]!.GetValue<string>(),
            CommandIdBase: baseCommandId,
            EffectKey: commandId,
            Objective: objective,
            SourceRefsJson: originalInputs["sourceRefs"]?.ToJsonString() is { Length: > 0 } refs && refs != "[]" ? refs : null,
            ExtraInputsJson: extraInputs.Count > 0 ? extraInputs.ToJsonString() : null,
            ActorEvidenceRef: originalData["actorEvidenceRef"]?.GetValue<string>()
                ?? $"kernel:steer:{commandId}",
            // Generation fencing: a re-dispatch is the next execution
            // generation of the assignment's work line (prior attempt's + 1);
            // terminal events from the fenced-over run stop matching it.
            ExecutionGeneration: latest?.Generation + 1 ?? 1);
        db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(redispatchIntent, now, envelopeValidator));
    }

    private void AppendDeliveryRow(
        FacilitatorDbContext db, System.Text.Json.Nodes.JsonObject envelope, string recipient, string commandId, Guid workId, DateTime now)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
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

    private static async Task WorkLockAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({workId}::text, 0))", ct);

    private async Task<int> BumpVersionAsync(FacilitatorDbContext db, Guid workId, string newState, long expectedVersion, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.works
                SET state = {newState},
                    version = version + 1,
                    updated_at = {now}
              WHERE work_id = {workId} AND version = {expectedVersion}
             """, ct);
    }

    /// <summary>Closure evidence: an artifact, a recorded manifest, an
    /// external work-link, or an explicit close basis. Anything else and a
    /// completion is an unevidenced claim.</summary>
    private static async Task<bool> HasClosureEvidenceAsync(
        FacilitatorDbContext db, Guid workId, TransitionOptions? options, string? reason, CancellationToken ct) =>
        options?.CloseReason is { Length: > 0 } || reason is { Length: > 0 }
        || options?.SuccessorWorkId is not null
        || await db.Artifacts.AsNoTracking().AnyAsync(a => a.WorkId == workId, ct)
        || await db.ResultManifests.AsNoTracking().AnyAsync(m => m.WorkId == workId, ct)
        || await db.WorkLinks.AsNoTracking().AnyAsync(l => l.WorkId == workId, ct);

    private static async Task AddWorkLinkAsync(
        FacilitatorDbContext db, Guid workId, Guid refWorkId, string kind, string? note, DateTime now, CancellationToken ct)
    {
        if (!await db.Works.AsNoTracking().AnyAsync(w => w.WorkId == refWorkId, ct))
        {
            throw new InvalidOperationException($"work link to unknown work {refWorkId}");
        }

        if (!await db.WorkLinks.AnyAsync(
                l => l.WorkId == workId && l.RefWorkId == refWorkId && l.Kind == kind, ct))
        {
            db.WorkLinks.Add(new WorkLinkEntity
            {
                LinkId = Guid.NewGuid(),
                WorkId = workId,
                RefWorkId = refWorkId,
                Kind = kind,
                Note = note,
                CreatedAt = now,
            });
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstAsync(ct),
                Kind = "work.linked",
                Actor = "kernel",
                Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["refWorkId"] = refWorkId.ToString(),
                    ["kind"] = kind,
                    ["note"] = note,
                }),
                CreatedAt = now,
            });
        }
    }

    private static Guid? ProjectTeamId(FacilitatorDbContext db, Guid? projectId) =>
        projectId is { } id
            ? db.Projects.AsNoTracking().Where(p => p.ProjectId == id).Select(p => p.TeamId).FirstOrDefault()
            : null;

    private static async Task<WorkView?> LoadAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(WorkProjection.View)
            .FirstOrDefaultAsync(ct);

    private OutboxEntity AppendWorkProgress(FacilitatorDbContext db, Guid workId, string note, string? causationId, DateTime createdAt)
    {
        var envelope = EnvelopeFactory.WorkProgress(workId, note, causationId);
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            // Kernel-emitted envelopes must be contract-valid; fail closed at
            // insert rather than poison the outbox (locked decision).
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        var row = new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = KernelLogChannel,
            AggregateType = "work",
            AggregateId = workId,
            EventType = EnvelopeFactory.WorkProgressType,
            CommandId = Guid.NewGuid().ToString("N"),
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = createdAt,
            Attempts = 0,
            CreatedAt = createdAt,
        };
        db.Outbox.Add(row);
        return row;
    }

    /// <summary>Completion hook: a terminal transition of a bound Work
    /// appends one <c>command.communication.reply.v1</c> to the bound
    /// conversation, inside the SAME transaction (the reply law; the
    /// deterministic <c>communication-reply:&lt;transitionEventId&gt;</c>
    /// commandId makes retries idempotent on the ledger). No binding or no
    /// registered provider ⇒ zero rows. When AiDeck is live the
    /// <see cref="CommunicationDecision"/> is consulted first: "silent" ⇒
    /// zero rows; "react" ⇒ the reaction envelope variant (expressible here
    /// via <c>ReactEnvelope</c>, same idempotent commandId); null / "reply"
    /// / AI-unavailable ⇒ the deterministic reply verbatim — the AI may
    /// suppress or soften an update, never swallow one it did not see.</summary>
    private async Task AppendProviderCompletionReplyAsync(
        FacilitatorDbContext db, Guid workId, WorkState to, string fromState, Guid transitionEventId, DateTime now, CancellationToken ct)
    {
        if (!StateGuard.IsTerminal(to))
        {
            return;
        }

        var binding = db.CommunicationBindings.AsNoTracking().FirstOrDefault(b => b.WorkId == workId);
        // Per-provider addressing: the reply rides the binding's own provider.
        if (binding is null || providers?.Find(binding.Provider) is not { EndpointId: { Length: > 0 } endpoint })
        {
            return;
        }

        var action = await CommunicationActionAsync(db, binding, to, fromState, ct);
        if (action == "silent")
        {
            return;
        }

        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            binding.Provider, null, binding.Channel, binding.RootMessageId, binding.ParentMessageId,
            null, "thread", null);
        var envelope = action == "react"
            ? Facilitator.Kernel.Integrations.ProviderEventStore.ReactEnvelope(
                target, Facilitator.Kernel.Integrations.ProviderEventStore.ReactionAckEmoji, $"communication-reply:{transitionEventId}", $"work/{workId}", now)
            : Facilitator.Kernel.Integrations.ProviderEventStore.ReplyEnvelope(
                target, $"Work reached {to.Wire()}.", $"communication-reply:{transitionEventId}", $"work/{workId}", now);
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = $"link.{endpoint}",
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            CommandId = $"communication-reply:{transitionEventId}",
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    /// <summary>The advisory communication answer for this completion ("reply"
    /// | "react" | "silent"), or null = no preference. Inert policy (absent
    /// <c>llm</c> row, disabled, AI seam absent) and
    /// <see cref="AiUnavailableException"/> both yield null — today's
    /// deterministic reply always stands when the model did not speak. The
    /// recency signal is the ledger: any AiDeck turn on this conversation in
    /// the last hour counts as an engaged conversation.</summary>
    private async Task<string?> CommunicationActionAsync(
        FacilitatorDbContext db, CommunicationBindingEntity binding, WorkState to, string fromState, CancellationToken ct)
    {
        if (aiPolicies is null || communication is null)
        {
            return null;
        }

        var policy = await aiPolicies.LoadAsync(db, ct);
        if (!policy.Enabled)
        {
            return null;
        }

        var recentConversation = await db.AiTurns.AsNoTracking().AnyAsync(
            t => t.ConversationKey == AiLedgerStore.ProviderKey(binding.Provider, binding.Channel, binding.RootMessageId)
                 && t.OccurredAt >= timeProvider.GetUtcNow().UtcDateTime.AddHours(-1), ct);
        try
        {
            return await communication.DecideAsync(
                "work.completion", $"work reached {to.Wire()} (from {fromState})", recentConversation, ct);
        }
        catch (AiUnavailableException)
        {
            return null;
        }
    }

    private static Task NotifyAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        Facilitator.Kernel.Shared.Persistence.PostgresNotifications.NotifyOutboxAsync(db, workId.ToString(), ct);
}
