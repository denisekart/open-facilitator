namespace Facilitator.Kernel.Integrations;

/// <summary>
/// <c>providers:mattermost</c> options section (Plan 22, locked decision 2): read
/// ONCE at composition like Storage/Memory. Absent section ⇒ <see cref="Enabled"/>
/// false ⇒ zero routes, zero stores, provider event classes refuse with
/// <c>unsupported</c>. The kernel boots provider-less by default; this is the
/// only always-on trace of the feature.
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>Configuration section carrying the Mattermost provider wiring.</summary>
    public const string SectionName = "Providers:Mattermost";

    /// <summary>Provider token every row/arms key on (schema-fixed today).</summary>
    public const string Mattermost = "mattermost";

    /// <summary>Explicit opt-in switch (absent ⇒ disabled).</summary>
    public bool Enabled { get; init; }

    /// <summary>Link endpoint id of the adapter (addressing: <c>link.&lt;EndpointId&gt;</c>).</summary>
    public string? EndpointId { get; init; }

    /// <summary>Tenant (team) identity rows key on.</summary>
    public string? Tenant { get; init; }

    /// <summary>Provider site URL: also the base for lazy bot-id resolution.</summary>
    public string? SiteUrl { get; init; }

    /// <summary>Bot provider-account id: the exact-match echo-suppression filter (§13.4).</summary>
    public string? BotUserId { get; init; }

    /// <summary>Bot REST token: when set and <see cref="BotUserId"/> is absent,
    /// the store resolves the bot id once via <c>GET /api/v4/users/me</c>
    /// (the live bot id is provider-generated, so compose cannot pin it).</summary>
    public string? BotToken { get; init; }

    /// <summary>Path holding the bot REST token, read lazily at each resolution
    /// attempt (compose: a provision one-shot writes it after Mattermost mints
    /// the token — PAT values cannot be pinned client-side, source-verified
    /// <c>UserAccessToken.PreSave</c> @ v11.11.1). <see cref="BotToken"/> wins
    /// when both are set.</summary>
    public string? BotTokenFile { get; init; }

    /// <summary>Assign interim (decision 7): when set, a mention that creates a Work
    /// also enqueues work.assign to this link endpoint (seed-lib SQL pattern, until
    /// capability routing lands in Plan 25).</summary>
    public string? AssignEndpointId { get; init; }

    /// <summary>Capability riding the interim assignment envelope.</summary>
    public string AssignCapabilityId { get; init; } = "analysis.summarize";

    /// <summary>Envelope targetId of the interim assignment.</summary>
    public string AssignTargetId { get; init; } = "mattermost-interim";
}

/// <summary>
/// Feature flag the admission gate reads: the provider event class is legal on
/// the wire, but a kernel without a registered provider must refuse it
/// fail-closed rather than settle it accepted-with-no-effect.
/// </summary>
public static class ProviderEventClass
{
    /// <summary>Type prefix every normalized provider event carries.</summary>
    public const string Prefix = "org.facilitator.event.provider.";

    /// <summary>Mattermost-normalized event types (the shipped adapter family).</summary>
    public const string MattermostPrefix = "org.facilitator.event.provider.mattermost.";

    /// <summary>True when the envelope type belongs to a provider family this
    /// kernel composition can act on.</summary>
    public static bool SupportedBy(ProviderOptions options, string type) =>
        options.Enabled && type.StartsWith(MattermostPrefix, StringComparison.Ordinal);

    /// <summary>True when the type is a provider-class event at all (regardless
    /// of registration) — the gate's fail-closed refusal trigger.</summary>
    public static bool IsProviderClass(string type) =>
        type.StartsWith(Prefix, StringComparison.Ordinal);
}
