namespace Facilitator.Kernel.Link;

/// <summary>
/// Link server settings (config group <c>Link</c>), validated once at
/// composition. Defaults per the Plan 03 locked decisions; tests override via
/// <c>UseSetting</c>.
/// </summary>
public sealed class LinkOptions
{
    /// <summary>Link-token lifetime in hours (Link:TokenLifetimeHours).</summary>
    public required int TokenLifetimeHours { get; init; }

    /// <summary>Per-session in-flight window (Link:MaxInFlight).</summary>
    public required int MaxInFlight { get; init; }

    /// <summary>Client heartbeat cadence advertised in Welcome (Link:HeartbeatSeconds, 5..600).</summary>
    public required int HeartbeatSeconds { get; init; }

    /// <summary>In-flight delivery lease before redelivery (Link:InFlightLeaseSeconds).</summary>
    public required int InFlightLeaseSeconds { get; init; }

    /// <summary>Protocol versions this server supports (handshake intersection).</summary>
    public static IReadOnlyList<string> SupportedProtocolVersions { get; } = ["v1"];

    /// <summary>Read + validate the <c>Link:*</c> keys.</summary>
    public static LinkOptions FromConfiguration(IConfiguration configuration)
    {
        var heartbeat = configuration.GetValue("Link:HeartbeatSeconds", 30);
        var maxInFlight = configuration.GetValue("Link:MaxInFlight", 16);
        if (heartbeat is < 5 or > 600)
        {
            throw new InvalidOperationException($"Link:HeartbeatSeconds must be 5..600 (got {heartbeat})");
        }

        if (maxInFlight < 1)
        {
            throw new InvalidOperationException($"Link:MaxInFlight must be >= 1 (got {maxInFlight})");
        }

        return new LinkOptions
        {
            TokenLifetimeHours = configuration.GetValue("Link:TokenLifetimeHours", 720),
            MaxInFlight = maxInFlight,
            HeartbeatSeconds = heartbeat,
            InFlightLeaseSeconds = configuration.GetValue("Link:InFlightLeaseSeconds", 120),
        };
    }
}
