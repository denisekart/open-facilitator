namespace Facilitator.Kernel.Integrations;

/// <summary>
/// One <c>Providers:&lt;Name&gt;</c> options section (Plan 22, widened by Plan 23
/// decision 1): read ONCE at composition like Storage/Memory. A provider is a
/// row of the <see cref="ProviderRegistry"/> only when its section exists and
/// <see cref="Enabled"/> is true; every adapter/connector instance is a
/// distinct link endpoint and reply addressing is always
/// <c>link.&lt;that provider's EndpointId&gt;</c> — never a global singleton.
/// Absent sections ⇒ zero routes, zero stores, provider event classes refuse
/// with <c>unsupported</c>. The kernel boots provider-less by default.
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>Configuration root of the per-provider sections.</summary>
    public const string RootSectionName = "Providers";

    /// <summary>Configuration section carrying the Mattermost provider wiring
    /// (the Plan 22 original; the registry now enumerates siblings).</summary>
    public const string SectionName = "Providers:Mattermost";

    /// <summary>Provider token every row/arms key on.</summary>
    public const string Mattermost = "mattermost";

    /// <summary>Provider token this instance carries (schema key: the section
    /// name lowercased). Assigned by <see cref="ProviderRegistry.Load"/>.</summary>
    public string Provider { get; set; } = Mattermost;

    /// <summary>Explicit opt-in switch (absent ⇒ disabled).</summary>
    public bool Enabled { get; init; }

    /// <summary>Link endpoint id of the adapter (addressing: <c>link.&lt;EndpointId&gt;</c>).</summary>
    public string? EndpointId { get; init; }

    /// <summary>Tenant (team) identity rows key on.</summary>
    public string? Tenant { get; init; }

    /// <summary>Provider site URL: also the base for lazy bot-id resolution.</summary>
    public string? SiteUrl { get; init; }

    /// <summary>Bot provider-account id: the exact-match echo-suppression filter (§13.4).
    /// Non-chat providers (Plan 23: forgejo) resolve the bot identity from this
    /// key ONLY — no REST probe; the adapter runs as the bot user.</summary>
    public string? BotUserId { get; init; }

    /// <summary>Bot REST token: when set and <see cref="BotUserId"/> is absent,
    /// the store resolves the bot id once via <c>GET /api/v4/users/me</c>
    /// (the live bot id is provider-generated, so compose cannot pin it).
    /// Mattermost-family only.</summary>
    public string? BotToken { get; init; }

    /// <summary>Path holding the bot REST token, read lazily at each resolution
    /// attempt (compose: a provision one-shot writes it after Mattermost mints
    /// the token — PAT values cannot be pinned client-side, source-verified
    /// <c>UserAccessToken.PreSave</c> @ v11.11.1). <see cref="BotToken"/> wins
    /// when both are set.</summary>
    public string? BotTokenFile { get; init; }

    /// <summary>Assign interim (decision 7): when set, an event that creates a Work
    /// also enqueues work.assign to this link endpoint (seed-lib SQL pattern, until
    /// capability routing lands in Plan 25).</summary>
    public string? AssignEndpointId { get; init; }

    /// <summary>Capability riding the interim assignment envelope.</summary>
    public string AssignCapabilityId { get; init; } = "analysis.summarize";

    /// <summary>Envelope targetId of the interim assignment; null ⇒
    /// <c>&lt;provider&gt;-interim</c> (the Plan 22 mattermost value).</summary>
    public string? AssignTargetId { get; init; }
}

/// <summary>
/// The N-provider registry (Plan 23, locked decision 1): one entry per
/// <c>Providers:&lt;Name&gt;</c> section present with <c>enabled: true</c>,
/// keyed by the provider token (section name lowercased) — the same token the
/// envelope type segment carries. Fail-closed per section: enabled without
/// addressing fails composition (Storage/Memory precedent). The ceiling is
/// plain options instances + one dictionary: no per-provider hosted services.
/// </summary>
public sealed class ProviderRegistry
{
    /// <summary>A registry with no providers (slim composition).</summary>
    public static readonly ProviderRegistry Empty = new(new Dictionary<string, ProviderOptions>());

    private readonly Dictionary<string, ProviderOptions> providers;

    internal ProviderRegistry(Dictionary<string, ProviderOptions> providers)
    {
        this.providers = providers;
    }

    /// <summary>Bound registry (case-insensitive provider-token keys).</summary>
    public static ProviderRegistry Load(IConfiguration configuration)
    {
        var registry = new Dictionary<string, ProviderOptions>(StringComparer.Ordinal);
        foreach (var section in configuration.GetSection(ProviderOptions.RootSectionName).GetChildren())
        {
            var options = section.Get<ProviderOptions>() ?? new ProviderOptions();
            if (!options.Enabled)
            {
                continue;
            }

            if (string.IsNullOrEmpty(options.EndpointId) || string.IsNullOrEmpty(options.Tenant))
            {
                throw new InvalidOperationException(
                    $"Providers:{section.Key} enabled but EndpointId/Tenant unset — fail-closed.");
            }

            options.Provider = section.Key.ToLowerInvariant();
            registry[options.Provider] = options;
        }

        return new ProviderRegistry(registry);
    }

    /// <summary>Number of enabled providers.</summary>
    public int Count => providers.Count;

    /// <summary>True when at least one provider section is enabled (route gate).</summary>
    public bool AnyEnabled => providers.Count > 0;

    /// <summary>Lookup by provider token (exact match — tokens are lowercase).</summary>
    public bool TryGet(string provider, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProviderOptions? options) =>
        providers.TryGetValue(provider, out options);

    /// <summary>Enabled options for a token, or null.</summary>
    public ProviderOptions? Find(string provider) =>
        providers.TryGetValue(provider, out var options) ? options : null;
}

/// <summary>
/// Feature flag the admission gate reads: the provider event class is legal on
/// the wire, but a kernel without a registered provider must refuse it
/// fail-closed rather than settle it accepted-with-no-effect. The provider
/// token is the type segment after the family prefix
/// (<c>org.facilitator.event.provider.&lt;provider&gt;.*</c>).
/// </summary>
public static class ProviderEventClass
{
    /// <summary>Type prefix every normalized provider event carries.</summary>
    public const string Prefix = "org.facilitator.event.provider.";

    /// <summary>Provider-family prefix for one token (3-segment-neutral: the
    /// remaining segments are <c>&lt;domain&gt;.&lt;action&gt;.v1</c> for
    /// multi-domain forges, <c>&lt;action&gt;.v1</c> for single-domain chat).</summary>
    public static string FamilyPrefix(string provider) =>
        Prefix + provider + ".";

    /// <summary>Mattermost-normalized event types (the Plan 22 adapter family).</summary>
    public const string MattermostPrefix = "org.facilitator.event.provider.mattermost.";

    /// <summary>The provider token parsed from the type segment, or null when
    /// the type is not provider-class (or carries no segment after the prefix).</summary>
    public static string? ProviderOf(string type)
    {
        if (!type.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = type[Prefix.Length..];
        var dot = rest.IndexOf('.');
        var token = dot < 0 ? rest : rest[..dot];
        return token.Length == 0 ? null : token.ToLowerInvariant();
    }

    /// <summary>True when the envelope type belongs to a provider family this
    /// kernel composition can act on: an enabled section exists for the
    /// parsed token AND the type carries that family prefix.</summary>
    public static bool SupportedBy(ProviderRegistry registry, string type) =>
        ProviderOf(type) is { Length: > 0 } token
        && registry.TryGet(token, out var options)
        && type.StartsWith(FamilyPrefix(options.Provider), StringComparison.Ordinal);

    /// <summary>True when the type is a provider-class event at all (regardless
    /// of registration) — the gate's fail-closed refusal trigger.</summary>
    public static bool IsProviderClass(string type) =>
        type.StartsWith(Prefix, StringComparison.Ordinal);
}
