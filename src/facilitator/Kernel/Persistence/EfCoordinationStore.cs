using System.Text.Json;

using Facilitator.Kernel.Domain;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Persistence;

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
    TimeProvider timeProvider) : ICoordinationStore
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
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
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

    private static Task NotifyAsync(FacilitatorDbContext db, Guid workId, CancellationToken ct) =>
        Facilitator.Kernel.Shared.Persistence.PostgresNotifications.NotifyOutboxAsync(db, workId.ToString(), ct);
}
