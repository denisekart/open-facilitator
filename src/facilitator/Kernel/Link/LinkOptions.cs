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

    /// <summary>Team roster slugs (Link:Teams) seeded into facilitator.teams at startup.</summary>
    public required IReadOnlyList<string> Teams { get; init; }

    /// <summary>Mode-(b) workload pre-registrations (Link:WorkloadIdentities).</summary>
    public required IReadOnlyList<WorkloadIdentityBinding> WorkloadIdentities { get; init; }

    /// <summary>Required audience of oidc: evidence JWTs (Link:Evidence:Audience).</summary>
    public required string EvidenceAudience { get; init; }

    /// <summary>Clock skew tolerated on oidc: evidence JWT lifetime checks (Link:Evidence:ClockSkewSeconds; default 30).</summary>
    public required int EvidenceClockSkewSeconds { get; init; }

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
            Teams = configuration.GetSection("Link:Teams").Get<string[]>() ?? [],
            WorkloadIdentities = configuration.GetSection("Link:WorkloadIdentities").Get<WorkloadIdentityBinding[]>() ?? [],
            EvidenceAudience = configuration.GetValue("Link:Evidence:Audience", "facilitator-link"),
            EvidenceClockSkewSeconds = configuration.GetValue("Link:Evidence:ClockSkewSeconds", 30),
        };
    }
}

/// <summary>Link:WorkloadIdentities element — one mode-(b) enrollment pre-registration.</summary>
public sealed class WorkloadIdentityBinding
{
    /// <summary>OIDC client id (the token's azp) allowed to enroll.</summary>
    public required string ClientId { get; init; }

    /// <summary>Logical endpoint this client may bind.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Team slug the enrollment binds to.</summary>
    public required string Team { get; init; }

    /// <summary>gateway | child, fixed by the binding.</summary>
    public required string Role { get; init; }

    /// <summary>Whether validated oidc: evidence may extend this endpoint's principal.</summary>
    public bool AcceptsDelegatedClaims { get; init; }
}
