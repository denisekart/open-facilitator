using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// EF Core implementation of the §20 coordination seam. Each mutation opens
/// one transaction, takes <c>pg_advisory_xact_lock(hashtext(work_id))</c> so
/// per-Work decisions serialize, applies a version-checked UPDATE (spec §7.4),
/// appends the timeline + outbox rows, and pokes <c>NOTIFY facilitator_outbox</c>
/// (hint only) — all before commit.
/// </summary>
public sealed class EfCoordinationStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider,
    Facilitator.Kernel.Integrations.ProviderRegistry? providers = null) : ICoordinationStore
{
    private const string KernelLogChannel = "kernel.log";

    /// <inheritdoc />
    public async Task<WorkView> CreateWorkAsync(string objective, Guid? projectId, string actor, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);

        var created = await LoadAsync(db, workId, ct);
        return created ?? throw new InvalidOperationException($"work {workId} vanished right after creation");
    }

    /// <inheritdoc />
    public async Task<TransitionResult> ApplyTransitionAsync(Guid workId, WorkState to, long expectedVersion, string actor, string? reason, CancellationToken ct)
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
            }),
            CreatedAt = now,
        });

        AppendWorkProgress(db, workId, $"work {current.State}→{to.Wire()}", causationId: null, createdAt: now);
        AppendProviderCompletionReply(db, workId, to, transitionEventId, now);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, workId, ct);
        await tx.CommitAsync(ct);

        return new TransitionResult.Ok((await LoadAsync(db, workId, ct))!);
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

    /// <summary>Plan 24(1b/1c): a steer reaches the owning gateway. The
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
    /// No live execution ⇒ §23.1's restart-with-context leg: a fresh
    /// <c>work.assign</c> re-dispatch reusing the ORIGINAL envelope's
    /// capability/target/inputs (the worker re-hydrates from the branch +
    /// PR lineage), keyed <c>work-redispatch:&lt;steerCommandId&gt;</c> so
    /// replay is idempotent on the unique command id. No assign row (a
    /// REST-created Work) ⇒ timeline record only, today's behavior.</summary>
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
            var steer = Facilitator.Kernel.Integrations.ProviderEnvelopes.Steer(
                workId,
                data.GetProperty("assignmentId").GetString() ?? latest.AssignmentId.ToString("N"),
                data.GetProperty("targetId").GetString() ?? "kernel",
                directive,
                contextVersion,
                now,
                $"work-steer:{commandId}");
            AppendDeliveryRow(db, steer, original.RecipientEndpointId, $"work-steer:{commandId}", workId, now);
            return;
        }

        // Re-dispatch: clone the original assign envelope verbatim, new
        // identity (fresh CloudEvent id + assignmentId + command id).
        var redispatch = System.Text.Json.Nodes.JsonNode.Parse(original.Envelope)!.AsObject();
        var newAssignment = Guid.NewGuid().ToString("N");
        var redispatchCommandId = $"work-redispatch:{commandId}";
        redispatch["id"] = Guid.NewGuid().ToString("N");
        redispatch["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        redispatch["subject"] = $"work/{workId}/assignment/{newAssignment}";
        var redispatchData = redispatch["data"]!.AsObject();
        redispatchData["commandId"] = redispatchCommandId;
        redispatchData["assignmentId"] = newAssignment;
        // The re-dispatched run has no steer channel of its own yet (the
        // review leg is the FIRST thing it will see), so the directive rides
        // its inputs: `steers` accumulates one entry per re-dispatch leg;
        // the harness folds them into the change + the "steer incorporated"
        // commentRequest (24 5, steering law).
        var inputs = redispatchData["inputs"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var steers = inputs["steers"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        steers.Add(directive.Length <= 2048 ? directive : directive[..2048]);
        inputs["steers"] = steers;
        redispatchData["inputs"] = inputs;
        AppendDeliveryRow(db, redispatch, original.RecipientEndpointId, redispatchCommandId, workId, now);
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

    private static async Task<WorkView?> LoadAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new WorkView(w.WorkId, w.ProjectId, w.Objective, w.State, w.Version, w.CreatedAt, w.UpdatedAt))
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

    /// <summary>Plan 22 completion hook: a terminal transition of a bound Work
    /// appends one <c>command.communication.reply.v1</c> to the bound
    /// conversation, inside the SAME transaction (the §13.2 reply law; the
    /// deterministic <c>communication-reply:&lt;transitionEventId&gt;</c>
    /// commandId makes retries idempotent on the ledger). No binding or no
    /// registered provider ⇒ zero rows.</summary>
    private void AppendProviderCompletionReply(
        FacilitatorDbContext db, Guid workId, WorkState to, Guid transitionEventId, DateTime now)
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

        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            binding.Provider, null, binding.Channel, binding.RootMessageId, binding.ParentMessageId,
            null, "thread", null);
        var envelope = Facilitator.Kernel.Integrations.ProviderEventStore.ReplyEnvelope(
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
            EventType = "org.facilitator.command.communication.reply.v1",
            CommandId = $"communication-reply:{transitionEventId}",
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    private static Task NotifyAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        Facilitator.Kernel.Shared.Persistence.PostgresNotifications.NotifyOutboxAsync(db, workId.ToString(), ct);
}
