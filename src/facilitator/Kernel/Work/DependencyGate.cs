using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>One DAG edge stored in <c>assignments.depends_on</c> (jsonb): a
/// sibling assignment UUID + the condition that releases the gate. The wire
/// form of <c>event.routing.requested.v1.dependsOnJson</c> is the same array
/// (<c>{ "assignmentId": "&lt;uuid&gt;", "condition": "completed|result_validated|approved" }</c>).</summary>
/// <param name="AssignmentId">The dependency assignment (same Work by construction).</param>
/// <param name="Condition">Release condition wire name.</param>
public sealed record DependencyEdge(Guid AssignmentId, string Condition);

/// <summary>
/// The dependsOn DAG gate. The funnel suppresses an assign
/// while its deps are unmet (<see cref="UnmetAsync"/> ⇒ the routing intent
/// requeues with the dispatcher's backoff — never dead-letters); satisfaction
/// arrives as a wake-law UPDATE on the sibling's completion / manifest
/// validation / approval decision
/// (<see cref="WakeGatedRoutingIntentsAsync"/>), which pulls the gated
/// intent's <c>available_at</c> forward so the next dispatcher pass
/// re-evaluates it. The evaluation is a pure read of the ledgers — no state
/// of its own, so re-evaluation is free and the DAG can never desync.
/// </summary>
public static class DependencyGate
{
    /// <summary>Release conditions (CHECK-equivalent vocabulary).</summary>
    public static readonly string[] Conditions = ["completed", "result_validated", "approved"];

    /// <summary>Parse a depends_on / dependsOnJson array. Invalid JSON, a
    /// non-array, a non-GUID id, or an unknown condition ⇒ null (the caller
    /// rejects: gate data must be exact, a malformed gate never silently
    /// passes an assign).</summary>
    public static List<DependencyEdge>? Parse(string? json)
    {
        if (json is not { Length: > 0 })
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return null;
            }

            var edges = new List<DependencyEdge>();
            foreach (var item in array.OfType<JsonObject>())
            {
                var raw = item["assignmentId"]?.GetValue<string>();
                var condition = item["condition"]?.GetValue<string>();
                if (!Guid.TryParse(raw, out var id)
                    || condition is not { Length: > 0 } cond
                    || !Conditions.Contains(cond, StringComparer.Ordinal)
                    || (edges.Count(e => e.AssignmentId == id) > 0))
                {
                    return null;
                }

                edges.Add(new DependencyEdge(id, cond));
            }

            // A self-edge can't be created via the REST law (deps reference
            // SIBLINGS), and a dep row that is its own gate never releases.
            return edges;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The unmet edges for a dependency set (empty ⇒ released). A
    /// dependency assignment that no longer resolves is UNMET forever —
    /// honest: the gate references a ledger row that must exist; the store's
    /// create-time validation rejects unknown deps, so a vanished dep means
    /// data corruption, not a pass.</summary>
    public static async Task<List<DependencyEdge>> UnmetAsync(
        FacilitatorDbContext db, string? dependsOnJson, CancellationToken ct)
    {
        var edges = Parse(dependsOnJson) ?? [];
        if (edges.Count == 0)
        {
            return [];
        }

        var ids = edges.Select(e => e.AssignmentId).ToArray();
        var rows = await db.Assignments.AsNoTracking()
            .Where(a => ids.Contains(a.AssignmentId))
            .Select(a => new
            {
                a.AssignmentId,
                a.Status,
                a.WorkId,
                Validated = db.ResultManifests.Any(m => m.AssignmentId == a.AssignmentId && m.ResultState == "validated"),
                Approved = db.Approvals.Any(p => p.WorkId == a.WorkId && (p.State == "approved" || p.State == "consumed")),
            })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.AssignmentId);

        // A missing dep row is UNMET (the doc law): sibling intents of one
        // create are dispatched in arbitrary order, and a gate that released
        // on "row not visible yet" would let the funnel race its own DAG.
        return edges.Where(e => !byId.TryGetValue(e.AssignmentId, out var row)
            || !Releases(row.Status, row.Validated, row.Approved, e.Condition))
            .ToList();

        static bool Releases(string status, bool validated, bool approved, string condition) =>
            condition switch
            {
                "completed" => status == "completed",
                "result_validated" => validated,
                _ => approved, // 'approved': an approval row decided approved (consumed keeps the decision)
            };
    }

    /// <summary>The wake law: a sibling terminal event (completed, failed,
    /// validated re-mark, approval decided) pulls every still-pending routing
    /// intent for the same Work forward so the funnel re-evaluates its gate
    /// NOW instead of at backoff. One UPDATE; a no-op when nothing is gated.</summary>
    public static async Task WakeGatedRoutingIntentsAsync(
        FacilitatorDbContext db, Guid workId, DateTime now, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.outbox SET available_at = {now}
              WHERE status = 'pending'
                AND event_type = {RoutingIntent.Type}
                AND work_id = {workId}
                AND available_at > {now}
             """, ct);
}

/// <summary>422 carrier for a create-time DAG the kernel cannot admit
/// (unknown sibling index, unknown condition, malformed reference, or a
/// dependency cycle). <c>IngressProcessor</c> maps it to
/// <see cref="Facilitator.Kernel.Shared.ProblemErrors.DagViolation"/>.</summary>
public sealed class DagViolationException(string detail) : Exception(detail);

/// <summary>Static DAG law for a create batch: validate + resolve the raw
/// <c>dependsOn</c> arrays against the batch's deterministic assignment
/// identities, so the funnel's <c>depends_on</c> join is exact from the first
/// pass. A sibling <c>index</c> resolves to that sibling's deterministic
/// assignment UUID (derivable before the funnel runs); an
/// <c>assignmentId</c> reference must be a GUID (rows are matched at gate
/// evaluation; a vanished dep stays honestly unmet forever).</summary>
public static class DagLaw
{
    /// <summary>The batch's resolved per-seed <c>depends_on</c> JSON (null =
    /// no deps). Throws <see cref="DagViolationException"/> on the first
    /// violation.</summary>
    public static string?[] ResolveCreateBatch(Guid workId, IReadOnlyList<RoutingIntentSeed> seeds)
    {
        var effectKeys = Enumerable.Range(0, seeds.Count)
            .Select(i => RoutingIntent.CreateEffectKey(workId, i)).ToArray();
        var resolved = new string?[seeds.Count];
        // edges[from][to] over batch indexes, for cycle detection.
        var edges = seeds.Select(_ => new HashSet<int>()).ToArray();
        for (var i = 0; i < seeds.Count; i++)
        {
            if (seeds[i].DependsOn is not { ValueKind: System.Text.Json.JsonValueKind.Array } deps)
            {
                continue;
            }

            var outArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in deps.EnumerateArray())
            {
                var condition = item.TryGetProperty("condition", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                    ? c.GetString() : null;
                if (condition is not { Length: > 0 } cond || !DependencyGate.Conditions.Contains(cond, StringComparer.Ordinal))
                {
                    throw new DagViolationException(
                        $"assignments[{i}].dependsOn: condition must be one of {string.Join('/', DependencyGate.Conditions)}");
                }

                Guid depId;
                if (item.TryGetProperty("index", out var idx) && idx.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var target = idx.GetInt32();
                    if (target < 0 || target >= seeds.Count)
                    {
                        throw new DagViolationException(
                            $"assignments[{i}].dependsOn: index {target} is not a sibling of this batch");
                    }

                    if (target == i)
                    {
                        throw new DagViolationException($"assignments[{i}].dependsOn: a sibling cannot gate itself");
                    }

                    edges[i].Add(target);
                    depId = RoutingIntent.AssignmentIdFor($"work-assign:{effectKeys[target]}");
                }
                else if (item.TryGetProperty("assignmentId", out var aid)
                    && aid.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(aid.GetString(), out var parsed))
                {
                    depId = parsed;
                }
                else
                {
                    throw new DagViolationException(
                        $"assignments[{i}].dependsOn: each entry needs a valid sibling 'index' or 'assignmentId'");
                }

                var dup = outArray.OfType<System.Text.Json.Nodes.JsonObject>()
                    .Any(e => e["assignmentId"]?.GetValue<string>() == depId.ToString());
                if (!dup)
                {
                    outArray.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["assignmentId"] = depId.ToString(),
                        ["condition"] = cond,
                    });
                }
            }

            resolved[i] = outArray.Count > 0 ? outArray.ToJsonString() : null;
        }

        DetectCycle(edges);
        return resolved;
    }

    /// <summary>DFS over the intra-batch index graph; a back edge is a
    /// <c>dependency-cycle</c> 422 (the whole request is rejected — a
    /// partially-gated batch would silently reorder execution).</summary>
    private static void DetectCycle(HashSet<int>[] edges)
    {
        var state = new int[edges.Length]; // 0 fresh, 1 in-stack, 2 done
        for (var start = 0; start < edges.Length; start++)
        {
            Visit(start);
        }

        void Visit(int node)
        {
            if (state[node] == 1)
            {
                throw new DagViolationException("assignments[].dependsOn: the batch contains a dependency cycle");
            }

            if (state[node] == 2)
            {
                return;
            }

            state[node] = 1;
            foreach (var next in edges[node])
            {
                Visit(next);
            }

            state[node] = 2;
        }
    }
}
