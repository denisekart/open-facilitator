using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The subscription set: { trigger event types of enabled definitions } ∪
/// { <c>wait.event</c> types of every running/waiting instance's pinned
/// definition } — a flow parked on <c>assignment.completed</c> wakes even
/// when no definition <i>triggers</i> on it. Cached in-memory with a short
/// TTL (rebuilt on boot, refreshed on use), so the emitter's per-event
/// fast path never touches the database unless the set may contain the
/// type; a set miss is a safe no-match, the TTL bounds staleness of a
/// just-enabled definition to a few seconds.
/// </summary>
public sealed class WorkflowSubscriptionRegistry(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ImmutableSet _set = new(ImmutableHashSet(), DateTime.MinValue);

    /// <summary>True when the set may contain <paramref name="eventType"/>
    /// (the cheap gate before the emitter's full match). Refreshes the
    /// cache when stale.</summary>
    public async Task<bool> MayContainAsync(string eventType, CancellationToken ct)
    {
        var current = _set;
        if (timeProvider.GetUtcNow().UtcDateTime - current.BuiltAt > Ttl)
        {
            await RebuildAsync(ct);
            current = _set;
        }

        return current.Types.Contains(eventType);
    }

    /// <summary>Rebuild from the database (startup, admin toggle, park or
    /// unpark — all cheap).</summary>
    public async Task RebuildAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var payload in await db.WorkflowDefinitions.AsNoTracking()
                         .Where(d => d.Enabled).Select(d => d.Payload).ToListAsync(ct))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("on", out var on)
                    && on.TryGetProperty("event", out var eventProp)
                    && eventProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    types.Add(eventProp.GetString()!);
                }
            }

            var pinned = db.WorkflowInstances.AsNoTracking()
                .Where(i => i.Status == "waiting")
                .Select(i => new { i.DefId, i.DefVersion });
            foreach (var pinnedRow in await pinned.ToListAsync(ct))
            {
                var payload = await db.WorkflowDefinitions.AsNoTracking()
                    .Where(d => d.DefId == pinnedRow.DefId && d.Version == pinnedRow.DefVersion)
                    .Select(d => d.Payload).FirstOrDefaultAsync(ct);
                if (payload is null)
                {
                    continue;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
                {
                    if (step.TryGetProperty("wait", out var wait)
                        && wait.TryGetProperty("event", out var awaited)
                        && awaited.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        types.Add(awaited.GetString()!);
                    }
                }
            }

            _set = new ImmutableSet(types, timeProvider.GetUtcNow().UtcDateTime);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HashSet<string> ImmutableHashSet() => new(StringComparer.Ordinal);

    private sealed record ImmutableSet(HashSet<string> Types, DateTime BuiltAt);
}
