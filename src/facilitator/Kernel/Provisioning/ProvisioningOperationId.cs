using System.Security.Cryptography;
using System.Text;

namespace Facilitator.Kernel.Provisioning;

/// <summary>
/// The replay-safety law: an operation id is the lowercase hex SHA-256 of the
/// pipe-joined tuple <c>key1|key2|generation|op</c>. The id identifies the
/// TRIGGER, not the workload — dedupe lives in the sweep's pending-op and
/// liveness checks, so independent episodes produce fresh ids (a killed
/// worker gets a relaunch envelope instead of an ON CONFLICT no-op) while a
/// double-swept trigger lands the identical id, collides on the
/// <c>provisioning_operations</c> PK and on the outbox
/// <c>command_id</c> unique index, and is a no-op end to end.
/// Arms and their tuples:
/// <list type="bullet">
/// <item>no-capacity: <c>workId|workloadId|0|start</c> — the triggering WORK
/// is the audit identity (generation literal <c>0</c>).</item>
/// <item>stalled / worker-lost: <c>workId|assignmentId|generation|start</c>
/// with the stalled/FAILED-attempt generation — a re-kill lands at
/// generation+1, so a fresh op id (the continuity arm).</item>
/// <item>min-workers / idle-shutdown (workload-level):
/// <c>workloadId|workloadId|N|start</c> / <c>...|N|stop</c> where N is the
/// COUNT of the workload's existing ledger rows at append time (same tx) —
/// the ledger is the episode counter, so ids stay monotonically distinct
/// across kernel restarts. The min-workers arm fires only on a workload with
/// ZERO rows (a boot-time shortfall, at most one per environment), so N is
/// 0 there; a relaunch after an idle stop arrives via a work-keyed arm.</item>
/// </list>
/// Assignment ids join in N-format (no hyphens), matching every other
/// kernel command-id ledger.
/// </summary>
public static class ProvisioningOperationId
{
    /// <summary>The no-capacity arm: keyed by the triggering work.</summary>
    public static string StartFor(Guid workId, string workloadId) =>
        Hash($"{workId:N}|{workloadId}|0|start");

    /// <summary>The stalled / worker-lost arms: keyed by the assignment and
    /// its (stalled or failed-attempt) generation.</summary>
    public static string StartFor(Guid workId, Guid assignmentId, int generation) =>
        Hash($"{workId:N}|{assignmentId:N}|{generation}|start");

    /// <summary>The min-workers arm (workload-level; N = the workload's
    /// current ledger-row count, 0 whenever the arm can fire).</summary>
    public static string StartFor(string workloadId, int ledgerRowCount) =>
        Hash($"{workloadId}|{workloadId}|{ledgerRowCount}|start");

    /// <summary>The idle-shutdown arm (workload-level; N = the workload's
    /// current ledger-row count, i.e. the stop-episode number).</summary>
    public static string StopFor(string workloadId, int ledgerRowCount) =>
        Hash($"{workloadId}|{workloadId}|{ledgerRowCount}|stop");

    private static string Hash(string tuple) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple))).ToLowerInvariant();
}
