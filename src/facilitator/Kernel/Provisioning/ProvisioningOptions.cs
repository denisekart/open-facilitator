namespace Facilitator.Kernel.Provisioning;

/// <summary>
/// Config section <c>Provision</c> (compose env keys are
/// <c>Provision__LAUNCHER_ENDPOINT</c> / <c>Provision__WORKLOADS__0__IMAGE</c>):
/// the launcher address + the per-workload container specs the processor
/// maps 1:1 onto the provision.start wire spec. An empty <c>Workloads</c>
/// list (the slim default) makes every sweep inert even when a policy row
/// exists — only workloads with BOTH a spec here and a <c>workload-scale</c>
/// policy row are managed. Bound as an ARRAY with an explicit
/// <c>WorkloadId</c> field (not a dictionary keyed by workload id): keys with
/// dots would become section boundaries and the compose
/// <c>__&lt;index&gt;__</c> interpolation needs positional indices, so the
/// array form is the lossless one.
/// </summary>
public sealed class ProvisioningOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Provision";

    /// <summary>Seconds between hosted sweep passes (the NudgeSweeper cadence
    /// law: test hosts park it long and drive <c>SweepOnceAsync</c>).</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>Launcher link endpoint (the outbox recipient is
    /// <c>link.&lt;LauncherEndpoint&gt;</c>). Empty = ops are still recorded
    /// but no envelope is emitted — honest manual-with-intent-ledger mode.</summary>
    public string LauncherEndpoint { get; set; } = string.Empty;

    /// <summary>Managed workload specs (array-bound; see type doc).</summary>
    public List<WorkloadSpec> Workloads { get; set; } = [];
}

/// <summary>One managed workload: the team it belongs to (wire-required) and
/// the container spec mapped 1:1 onto <c>command.provision.start.v1</c>'s
/// <c>spec</c>. Env values may be a literal or a <c>$VARNAME</c> indirection
/// — the KERNEL never expands it; the launcher does.</summary>
public sealed class WorkloadSpec
{
    /// <summary>Workload id = the link endpoint id the worker binds when it
    /// comes online (liveness authority).</summary>
    public string WorkloadId { get; set; } = string.Empty;

    /// <summary>Owning team slug (matches routing decisions' team).</summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>Container image reference.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Entrypoint override (null = image default).</summary>
    public string[]? Entrypoint { get; set; }

    /// <summary>Docker network name (null = launcher default).</summary>
    public string? Network { get; set; }

    /// <summary>Environment (literal or <c>$VAR</c> indirection; kernel never expands).</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Bind mounts.</summary>
    public List<VolumeSpec> Volumes { get; set; } = [];

    /// <summary>Labels MERGED OVER the default <c>{ofac.workload: &lt;id&gt;}</c>.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>SIGTERM→SIGKILL grace (wire: 0..300).</summary>
    public int? StopGraceSeconds { get; set; }
}

/// <summary>One bind mount (wire <c>spec.volumes[]</c>).</summary>
public sealed class VolumeSpec
{
    /// <summary>Host path or named volume.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Container path.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Read-only mount (null = writable).</summary>
    public bool? ReadOnly { get; set; }
}
