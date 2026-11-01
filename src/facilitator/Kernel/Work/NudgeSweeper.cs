using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The bounded nudge (§5.3): a host service that sweeps live assignments for
/// silence and re-prods the owning gateway at most <see cref="NudgeCap"/>
/// times per stall episode. The policy is an <c>admin_config</c> row
/// (<c>nudge-policy</c>, <c>{"staleAfterMinutes": N}</c>) — no row means no
/// nudging at all (silent, the pre-Phase-B behavior). Silence for an
/// assignment is measured from the freshest observable activity for its
/// Work: the attempt start, any timeline event, any inbound inbox row, any
/// acked command delivery. An endpoint without a live session lease is
/// skipped entirely (offline law: nudging a dead gateway just burns budget).
/// Each nudge is a <c>command.work.nudge.v1</c> outbox row to the endpoint
/// that carried the assignment (the outbox recipient IS the ownership
/// ledger, the steer-arm law); a gateway without a nudge handler answers
/// honestly and the receipt settles the row either way. After a nudge, a
/// progress refresh opens the NEXT stall episode: the budget is per episode,
/// "stalled" is derived from the ledger (nudge_count ≥ cap on the latest
/// episode) — no new assignment state.
/// </summary>
public sealed class NudgeSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider,
    ILogger<NudgeSweeper> logger,
    Facilitator.Kernel.Work.NudgeSweeper.NudgeOptions options) : BackgroundService
{
    /// <summary>§5.3: three nudges per stall episode, then stalled.</summary>
    public const int NudgeCap = 3;

    /// <summary>admin_config resource carrying <c>staleAfterMinutes</c>.</summary>
    public const string PolicyResource = "nudge-policy";

    /// <summary>Config section for the sweep loop (the GC-loop precedent):
    /// hosted passes fire on <c>Nudge:SweepIntervalSeconds</c>; test hosts
    /// set it long and drive <see cref="SweepOnceAsync"/> manually.</summary>
    public sealed class NudgeOptions
    {
        /// <summary>Config section name.</summary>
        public const string SectionName = "Nudge";

        /// <summary>Seconds between hosted sweep passes.</summary>
        public int SweepIntervalSeconds { get; set; } = 60;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.SweepIntervalSeconds)), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                // The sweep is advisory: one bad pass must never kill the loop.
                logger.LogWarning(ex, "nudge sweep pass failed");
            }
        }
    }

    /// <summary>One sweep (public for fake-clock tests): evaluate every live
    /// assignment against the policy, emit budgeted nudges, record episodes.
    /// Returns the number of nudges emitted.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var staleAfter = await PolicyMinutesAsync(db, ct);
        if (staleAfter is not { } minutes)
        {
            return 0;
        }

        var cutoff = now.AddMinutes(-minutes);
        var live = await db.Assignments.AsNoTracking()
            .Where(a => a.Status == "accepted" || a.Status == "running" || a.Status == "input_required")
            .Select(a => new { a.AssignmentId, a.WorkId, a.CurrentExecutionGeneration })
            .ToListAsync(ct);
        if (live.Count == 0)
        {
            return 0;
        }

        var workIds = live.Select(a => a.WorkId).Distinct().ToArray();
        var lastActivity = await LastActivityByWorkAsync(db, workIds, ct);
        var openAttempts = await db.ExecutionAttempts.AsNoTracking()
            .Where(e => workIds.Contains(e.Assignment!.WorkId)
                && !ExecutionLedger.AttemptTerminal.Contains(e.Status))
            .GroupBy(e => e.AssignmentId)
            .Select(g => new { AssignmentId = g.Key, StartedAt = g.Max(e => e.StartedAt) })
            .ToListAsync(ct);
        var startedBy = openAttempts.ToDictionary(a => a.AssignmentId, a => a.StartedAt);

        var nudgesEmitted = 0;
        foreach (var a in live)
        {
            if (!startedBy.TryGetValue(a.AssignmentId, out var startedAt))
            {
                continue; // never actually started (routed pre-receipt): not a stall
            }

            var activity = startedAt;
            if (lastActivity.TryGetValue(a.WorkId, out var seen) && seen > activity)
            {
                activity = seen;
            }

            if (activity > cutoff)
            {
                continue; // alive
            }

            // Episode law (§5.3): one episode per stall spell. The latest
            // ledger row's episode continues while the assignment stays
            // silent; observed progress past the episode's recorded activity
            // opens the NEXT episode (the budget is per episode).
            // Tracked: the episode's ledger row gets its count incremented
            // in place, so the read must attach.
            var history = await db.Nudges
                .Where(n => n.AssignmentId == a.AssignmentId && n.Generation == a.CurrentExecutionGeneration)
                .ToListAsync(ct);
            var latest = history.OrderByDescending(n => n.StallEpisode).FirstOrDefault();
            var episode = latest is null || latest.LastProgressAt < activity
                ? (latest?.StallEpisode ?? 0) + 1
                : latest.StallEpisode;
            var ledger = history.FirstOrDefault(n => n.StallEpisode == episode);
            if (ledger is not null && ledger.NudgeCount >= NudgeCap)
            {
                continue; // stalled: budget spent, awaiting progress or a human
            }

            var owner = await OwningEndpointAsync(db, a.WorkId, a.AssignmentId, ct);
            if (owner is not { Length: > 0 })
            {
                continue; // no command ever rode to an endpoint (synthetic lane)
            }

            // Offline law: no live session lease ⇒ nobody to nudge; the
            // session's return refreshes activity (its deliveries ack again).
            var online = await db.LinkSessions.AsNoTracking()
                .AnyAsync(s => s.LogicalEndpointId == owner && s.LeaseUntil > now, ct);
            if (!online)
            {
                continue;
            }

            var count = ledger?.NudgeCount ?? 0;
            var commandId = $"work-nudge:{a.AssignmentId:N}:{a.CurrentExecutionGeneration}:{episode}:{count + 1}";
            var envelope = ProviderEnvelopes.Nudge(
                a.WorkId, a.AssignmentId.ToString("N"), owner, episode,
                a.CurrentExecutionGeneration, now, commandId);
            var json = envelope.ToJsonString();
            var verdict = envelopeValidator.Validate(json);
            if (!verdict.Ok)
            {
                throw new InvalidOperationException($"nudge envelope rejected: {verdict.ErrorClass}: {verdict.Message}");
            }

            db.Outbox.Add(new OutboxEntity
            {
                OutboxId = Guid.NewGuid(),
                RecipientEndpointId = $"link.{owner}",
                AggregateType = "work",
                AggregateId = a.WorkId,
                EventType = "org.facilitator.command.work.nudge.v1",
                CommandId = commandId,
                Envelope = json,
                WorkId = a.WorkId,
                Status = "pending",
                AvailableAt = now,
                Attempts = 0,
                CreatedAt = now,
            });

            if (ledger is null)
            {
                db.Nudges.Add(new NudgeLedgerEntity
                {
                    AssignmentId = a.AssignmentId,
                    Generation = a.CurrentExecutionGeneration,
                    StallEpisode = episode,
                    NudgeCount = 1,
                    LastNudgeAt = now,
                    LastProgressAt = activity,
                });
            }
            else
            {
                ledger.NudgeCount = count + 1;
                ledger.LastNudgeAt = now;
            }

            await db.SaveChangesAsync(ct);
            await PostgresNotifications.NotifyOutboxAsync(db, a.WorkId.ToString(), ct);
            nudgesEmitted++;
        }

        return nudgesEmitted;
    }

    /// <summary>The policy read: minutes of silence before a nudge; null =
    /// no policy row (or unparseable) = no nudging.</summary>
    internal static async Task<int?> PolicyMinutesAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == PolicyResource)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload)?["staleAfterMinutes"]?.GetValue<int>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The freshest activity timestamp per Work over every ledger an
    /// execution can touch: timeline rows, inbound inbox rows, and acked
    /// command deliveries for the work.</summary>
    private static async Task<Dictionary<Guid, DateTime>> LastActivityByWorkAsync(
        FacilitatorDbContext db, Guid[] workIds, CancellationToken ct)
    {
        var timeline = await db.WorkEvents.AsNoTracking()
            .Where(e => workIds.Contains(e.WorkId))
            .GroupBy(e => e.WorkId)
            .Select(g => new { WorkId = g.Key, Last = g.Max(e => e.CreatedAt) })
            .ToListAsync(ct);
        var inbox = await db.Inbox.AsNoTracking()
            .Where(i => i.WorkId != null && workIds.Contains(i.WorkId!.Value))
            .GroupBy(i => i.WorkId!.Value)
            .Select(g => new { WorkId = g.Key, Last = g.Max(i => i.ReceivedAt) })
            .ToListAsync(ct);
        var acks = await db.LinkDeliveries.AsNoTracking()
            .Where(d => d.AckedAt != null)
            .Join(db.Outbox.AsNoTracking().Where(o => o.WorkId != null && workIds.Contains(o.WorkId!.Value)),
                d => d.DeliveryId, o => o.OutboxId, (d, o) => new { WorkId = o.WorkId!.Value, d.AckedAt })
            .GroupBy(x => x.WorkId)
            .Select(g => new { WorkId = g.Key, Last = g.Max(x => x.AckedAt) })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, DateTime>();
        foreach (var r in timeline)
        {
            map[r.WorkId] = r.Last;
        }

        foreach (var r in inbox)
        {
            if (!map.TryGetValue(r.WorkId, out var seen) || r.Last > seen)
            {
                map[r.WorkId] = r.Last;
            }
        }

        foreach (var r in acks)
        {
            var last = r.Last ?? default;
            if (last != default && (!map.TryGetValue(r.WorkId, out var seen) || last > seen))
            {
                map[r.WorkId] = last;
            }
        }

        return map;
    }

    /// <summary>The endpoint that carried this assignment (latest assign or
    /// redispatch row's recipient), the same ownership read the steer arm uses:
    /// the work's assign-family rows come out of SQL, the envelope's
    /// assignmentId is matched in memory (jsonb has no translatable LIKE).</summary>
    private static async Task<string?> OwningEndpointAsync(FacilitatorDbContext db, Guid workId, Guid assignmentId, CancellationToken ct)
    {
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.WorkId == workId
                && o.EventType == "org.facilitator.command.work.assign.v1"
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Envelope, o.RecipientEndpointId })
            .ToListAsync(ct);
        // Same read-back as the steer arm: parse the envelope, do not text-match
        // it (jsonb output normalizes whitespace).
        var needle = assignmentId.ToString("N");
        var row = rows.FirstOrDefault(r => HasAssignmentId(r.Envelope, needle))?.RecipientEndpointId;
        return row?.StartsWith("link.", StringComparison.Ordinal) == true ? row["link.".Length..] : row;
    }

    /// <summary>True when an assign-family envelope's data carries exactly
    /// this assignment id (the ownership ledger read-back).</summary>
    private static bool HasAssignmentId(string envelope, string assignmentIdN)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("assignmentId", out var id)
                && id.GetString() == assignmentIdN;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
