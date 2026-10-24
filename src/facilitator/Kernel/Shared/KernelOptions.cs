namespace Facilitator.Kernel.Shared;

/// <summary>
/// Kernel-wide settings read straight from configuration (no options
/// machinery at this scale).
/// </summary>
public static class KernelOptions
{
    /// <summary>
    /// Replica identity stamped on outbox leases and link session rows
    /// (config <c>Kernel:ReplicaId</c>, fallback machine-name/GUID). Single
    /// source: the dispatcher and the link session service must agree on it.
    /// </summary>
    public static string ReplicaId(IConfiguration configuration) =>
        configuration["Kernel:ReplicaId"] ?? $"{Environment.MachineName}/{Guid.NewGuid():N}";
}
