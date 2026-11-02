using System.Text.Json.Nodes;

namespace Facilitator.Kernel.Provisioning;

/// <summary>
/// Kernel→launcher command envelopes: the full CloudEvents
/// envelope for <c>org.facilitator.command.provision.start.v1</c> /
/// <c>.stop.v1</c> (dataschema per the wire, subject <c>workload/&lt;id&gt;</c>,
/// source <c>urn:facilitator:instance:&lt;Kernel:ReplicaId&gt;</c> — this
/// command is emitted BY ONE replica's sweep, the replica id names it).
/// <c>commandId == operationId ==</c> the deterministic op id: the outbox
/// ledger dedupe and the launcher's own dedupe read the same key.
/// The kernel never expands <c>$VAR</c> env indirections — the launcher does.
/// </summary>
public static class ProvisionEnvelopes
{
    /// <summary>Wire type of the start command.</summary>
    public const string StartType = "org.facilitator.command.provision.start.v1";

    /// <summary>Wire type of the stop command.</summary>
    public const string StopType = "org.facilitator.command.provision.stop.v1";

    /// <summary>Start command: the workload's configured spec mapped 1:1
    /// (labels merged OVER the default <c>{ofac.workload: &lt;id&gt;}</c>).</summary>
    public static JsonObject Start(
        string opId, string workloadId, string team, string reason,
        WorkloadSpec spec, string source, DateTime now, TimeSpan deadlineWindow) =>
        Envelope(StartType, opId, workloadId, team, reason, source, now, deadlineWindow, Spec(spec, workloadId));

    /// <summary>Stop command: the same envelope minus the spec.</summary>
    public static JsonObject Stop(
        string opId, string workloadId, string team, string reason,
        string source, DateTime now, TimeSpan deadlineWindow) =>
        Envelope(StopType, opId, workloadId, team, reason, source, now, deadlineWindow, spec: null);

    private static JsonObject Envelope(
        string type, string opId, string workloadId, string team, string reason,
        string source, DateTime now, TimeSpan deadlineWindow, JsonObject? spec)
    {
        var data = new JsonObject
        {
            ["commandId"] = opId,
            ["operationId"] = opId,
            ["workloadId"] = workloadId,
            ["team"] = team,
            ["actorEvidenceRef"] = $"kernel:provisioning:{reason}",
            ["deadline"] = (now + deadlineWindow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["reason"] = reason,
        };
        if (spec is not null)
        {
            data["spec"] = spec;
        }

        return new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = source,
            ["type"] = type,
            ["subject"] = $"workload/{workloadId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = $"https://schemas.openfacilitator.org/v1/{type["org.facilitator.".Length..]}.json",
            ["data"] = data,
        };
    }

    private static JsonObject Spec(WorkloadSpec spec, string workloadId)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["ofac.workload"] = workloadId };
        foreach (var (k, v) in spec.Labels)
        {
            labels[k] = v;
        }

        var s = new JsonObject
        {
            ["image"] = spec.Image,
            ["labels"] = new JsonObject(labels.ToDictionary(p => p.Key, p => (JsonNode?)p.Value)),
        };
        if (spec.Entrypoint is { Length: > 0 } entrypoint)
        {
            s["entrypoint"] = new JsonArray([.. entrypoint.Select(v => (JsonNode)v)]);
        }

        if (spec.Network is { Length: > 0 } network)
        {
            s["network"] = network;
        }

        if (spec.Env.Count > 0)
        {
            s["env"] = new JsonObject(spec.Env.ToDictionary(p => p.Key, p => (JsonNode?)p.Value));
        }

        if (spec.Volumes.Count > 0)
        {
            var volumes = new JsonArray();
            foreach (var v in spec.Volumes)
            {
                var volume = new JsonObject { ["source"] = v.Source, ["target"] = v.Target };
                if (v.ReadOnly is { } readOnly)
                {
                    volume["readOnly"] = readOnly;
                }

                volumes.Add(volume);
            }

            s["volumes"] = volumes;
        }

        if (spec.StopGraceSeconds is { } grace)
        {
            s["stopGraceSeconds"] = grace;
        }

        return s;
    }
}
