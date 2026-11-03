using System.Text.Json;

namespace Facilitator.Kernel.Integrations;

/// <summary>
/// One <c>Providers:&lt;name&gt;</c> options section: read ONCE at composition like
/// Storage/Memory. A provider is a row of the <see cref="ProviderRegistry"/>
/// only when its section exists and <see cref="Enabled"/> is true; the section
/// key lowercased IS the provider token — provider is configuration data, never
/// kernel vocabulary. Every adapter/connector instance is a distinct link
/// endpoint and reply addressing is always <c>link.&lt;that provider's
/// EndpointId&gt;</c> — never a global singleton. Absent sections ⇒ zero routes,
/// zero stores, provider event classes refuse with <c>unsupported</c>. The
/// kernel boots provider-less by default; human-plane provider routes exist
/// only when at least one section is enabled. The <c>POST /api/ingress/webhooks/{provider}</c>
/// sink fans raw forge deliveries out to <c>link.&lt;EndpointId&gt;</c> as
/// <c>command.provider.webhook.v1</c> rows: raw bytes + verbatim headers ride
/// untouched, so signature verification stays provider-local in the adapter.
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>Configuration root of the per-provider sections.</summary>
    public const string RootSectionName = "Providers";

    /// <summary>Provider token this instance carries (schema key: the section
    /// name lowercased). Assigned by <see cref="ProviderRegistry.Load"/>.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Explicit opt-in switch (absent ⇒ disabled).</summary>
    public bool Enabled { get; init; }

    /// <summary>Link endpoint id of the adapter (addressing: <c>link.&lt;EndpointId&gt;</c>).</summary>
    public string? EndpointId { get; init; }

    /// <summary>Tenant (team) identity rows key on.</summary>
    public string? Tenant { get; init; }

    /// <summary>Provider site URL (pure config data; consumed by provider-side
    /// overlays and operators, never parsed by the kernel).</summary>
    public string? SiteUrl { get; init; }

    /// <summary>Bot provider-account id: the exact-match echo-suppression filter.
    /// When set, no probe runs — the adapter runs as the bot user.</summary>
    public string? BotUserId { get; init; }

    /// <summary>Full URL the kernel GETs (once, with <c>Authorization: Bearer</c>
    /// from <see cref="BotToken"/>/<see cref="BotTokenFile"/>) to resolve the bot
    /// id when <see cref="BotUserId"/> is unset; the response's root <c>id</c> is
    /// the bot account id. The probe path is provider REST shape, so it lives
    /// here as config data — the kernel holds no vendor API vocabulary. Read by
    /// <c>ProviderEventStore.ResolveBotIdAsync</c>.</summary>
    public string? BotIdentityProbeUrl { get; init; }

    /// <summary>Reply appended to the audit of an unlinked mention (the "how to
    /// link" hint). Null ⇒ an unlinked mention is audited only, no reply. Read
    /// by <c>ProviderEventStore.MentionArmAsync</c>.</summary>
    public string? LinkHintText { get; init; }

    /// <summary>Bot REST token: when set and <see cref="BotUserId"/> is absent,
    /// the store resolves the bot id once via GET <see cref="BotIdentityProbeUrl"/>
    /// (the live bot id is provider-generated, so compose cannot pin it).</summary>
    public string? BotToken { get; init; }

    /// <summary>Path holding the bot REST token, read lazily at each resolution
    /// attempt (compose: a provision one-shot writes it after the provider mints
    /// the token — PAT values cannot be pinned client-side). <see cref="BotToken"/>
    /// wins when both are set.</summary>
    public string? BotTokenFile { get; init; }

    /// <summary>Assign interim: when set, an event that creates a Work also
    /// enqueues work.assign to this link endpoint (seed-lib SQL pattern, until
    /// capability routing lands).</summary>
    public string? AssignEndpointId { get; init; }

    /// <summary>Capability riding the interim assignment envelope.</summary>
    public string AssignCapabilityId { get; init; } = "analysis.summarize";

    /// <summary>Envelope targetId of the interim assignment; null ⇒
    /// <c>&lt;provider&gt;-interim</c>.</summary>
    public string? AssignTargetId { get; init; }

    /// <summary>Label-routed assign rules: when a Work-creating issue event
    /// carries a rule's label (case-insensitive, read out of the event's
    /// verbatim <c>payload</c>), the interim assign rides the rule's
    /// capability/endpoint instead of the global
    /// <see cref="AssignEndpointId"/>/<see cref="AssignCapabilityId"/> pair.
    /// A rule with a blank endpoint falls back to the global endpoint.
    /// Interim routing until capability routing lands.</summary>
    public List<AssignRule> AssignRules { get; init; } = [];

    /// <summary>The completed-manifest arm of <c>ResultIngress</c> reads
    /// <c>structuredResult.prRequest</c> / <c>commentRequests</c> only when
    /// the completing assignment's capability equals this value (provider-
    /// scoped: other providers ignore the manifest field). Null ⇒ the reader
    /// is off for this provider.</summary>
    public string? PrRequestCapability { get; init; }
}

/// <summary>One <c>Providers:&lt;name&gt;:AssignRules[n]</c> row: trigger
/// label ⇒ capability + owning link endpoint for the interim assign.</summary>
public sealed class AssignRule
{
    /// <summary>Issue label (case-insensitive) that selects this rule.</summary>
    public string? Label { get; init; }

    /// <summary>Capability id riding the assign envelope when this rule fires.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>Link endpoint id the assign is addressed to (blank ⇒ the
    /// provider's global <c>AssignEndpointId</c>).</summary>
    public string? EndpointId { get; init; }

    /// <summary>Envelope targetId override; null ⇒ the provider default.</summary>
    public string? TargetId { get; init; }

    /// <summary>Team slug the rule-matched Work is created under (blank ⇒
    /// the ingesting workload's team). The owning team must match the
    /// endpoint's fleet: the result-manifest ledger team-scopes a work's
    /// artifacts, so a cross-team worker could never record its manifest.</summary>
    public string? Team { get; init; }
}

/// <summary>
/// The N-provider registry: one entry per <c>Providers:&lt;name&gt;</c> section
/// present with <c>enabled: true</c>, keyed by the provider token (section name
/// lowercased) — the same token the envelope's <c>data.provider</c> carries.
/// Fail-closed per section: enabled without addressing fails composition
/// (Storage/Memory precedent). The ceiling is plain options instances + one
/// dictionary: no per-provider hosted services.
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

    /// <summary>Enabled options, any token (the completed-manifest reader's
    /// provider-wide capability scan).</summary>
    public IEnumerable<ProviderOptions> All() => providers.Values;
}

/// <summary>
/// The closed central provider-domain event type set the admission gate and
/// kernel arms switch on. Types name the integration DOMAIN (chat, issues,
/// change requests, pipelines) — never a vendor: the provider is data
/// (<c>data.provider</c>), so a new forge ships as adapter + config section
/// with zero core change. A provider-class type with no registered provider,
/// or outside the six, refuses fail-closed rather than settle
/// accepted-with-no-effect.
/// </summary>
public static class ProviderEventClass
{
    /// <summary>Type prefix every normalized provider event carries (routing:
    /// a vendor-shaped type from a stale adapter still reaches the provider
    /// path and gets a typed refusal, not the generic inbox).</summary>
    public const string Prefix = "org.facilitator.event.provider.";

    /// <summary>Chat family: a plain message the adapter deemed worth relaying.</summary>
    public const string Message = Prefix + "message.v1";

    /// <summary>Chat family: an @-mention of the bot identity.</summary>
    public const string Mention = Prefix + "mention.v1";

    /// <summary>Chat family: an emoji reaction on a post.</summary>
    public const string Reaction = Prefix + "reaction.v1";

    /// <summary>Issue-tracker domain.</summary>
    public const string Issue = Prefix + "issue.v1";

    /// <summary>Change-request domain (forge-neutral: merge/pull requests map in).</summary>
    public const string ChangeRequest = Prefix + "change_request.v1";

    /// <summary>Pipeline domain (CI/commit-status verdicts map in).</summary>
    public const string Pipeline = Prefix + "pipeline.v1";

    /// <summary>The closed set of central domain types the kernel acts on.</summary>
    static readonly string[] centralTypes = [Message, Mention, Reaction, Issue, ChangeRequest, Pipeline];

    /// <summary>True when the type is one of the six central domain types.</summary>
    public static bool IsCentralType(string type) =>
        Array.IndexOf(centralTypes, type) >= 0;

    /// <summary>The provider token from an envelope payload's <c>data.provider</c>
    /// (schema-required; null-guarded for defense), or null when absent.</summary>
    public static string? ProviderOfPayload(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("provider", out var provider)
            || provider.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = provider.GetString();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>True when the type is a provider-class event at all (regardless
    /// of registration or central membership) — the gate's fail-closed refusal
    /// trigger.</summary>
    public static bool IsProviderClass(string type) =>
        type.StartsWith(Prefix, StringComparison.Ordinal);
}
