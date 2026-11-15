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

    /// <summary>Outbound media arm: when true, a completion reply that reaches
    /// a terminal-completed state attaches the work's recorded-manifest output
    /// artifacts to the <c>communication.reply.v1</c>/<c>post.v1</c> envelope
    /// (subject to the endpoint's declared <c>media</c> capability facet and
    /// the media policy). Dropped or unsupported candidates never vanish
    /// silently: the reply text carries a brokered link line and every media
    /// act writes one audit row (<c>media.reply</c>/<c>media.degrade</c>).
    /// Default false: no route attaches bytes until its operator opts in.</summary>
    public bool SendManifestArtifacts { get; init; }

    /// <summary>Provider-level default context block (mirrors how
    /// <see cref="AssignEndpointId"/> defaults what <see cref="AssignRules"/>
    /// may override per rule): the rule's own <c>context</c> wins when it
    /// carries one.</summary>
    public List<ContextSource> AssignContext { get; init; } = [];

    /// <summary>Chat-plane label routing: when true, the chat Work-creation
    /// paths (mention arm, slash plane, decode sink) match
    /// <see cref="AssignRules"/> against the event's adapter-normalized
    /// top-level <c>labels</c> exactly as the issue arm does, and an
    /// unmatched event keeps the global pair. Default false: providers whose
    /// labels live on issues only (forges) take the unchanged code path.</summary>
    public bool ChatLabelRouting { get; init; }

    /// <summary>Chat-consent tracker opt-in (C7): when set, an entitled chat
    /// consent decision dispatches the tracker record through the provider
    /// plane with this capability (<c>vcs.issue.create</c> plus a
    /// <c>vcs.issue.comment</c> carrying decision, decider, digest). Null ⇒ no
    /// tracker is bound: the decision records with the person's proven
    /// identity plus a timeline row naming the missing tracker.</summary>
    public string? IssueRequestCapability { get; init; }

    /// <summary>Link endpoint executing <see cref="IssueRequestCapability"/>;
    /// null ⇒ <c>&lt;provider&gt;-connector</c> (the connector convention).</summary>
    public string? IssueRequestEndpointId { get; init; }

    /// <summary>DEPRECATED (Phase 4, kept one release): the completed-manifest
    /// arm of <c>ResultIngress</c> reads <c>structuredResult.prRequest</c> /
    /// <c>commentRequests</c> only when the completing assignment's capability
    /// equals this value (provider-scoped). At boot the value IMPORTS a
    /// scoped, enabled copy of the workflow gallery's pr-on-manifest
    /// (defId <c>pr-on-manifest-&lt;provider&gt;</c>, source='derived') that
    /// encodes the same scope as data; while any such copy is
    /// enabled-and-live the arm defers to the workflow plane entirely.
    /// Null ⇒ no import: the arm reader is off for this provider.</summary>
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

    /// <summary>The declarative context block folded into this rule's assign
    /// envelope (see <see cref="ContextSource"/>): routes without a block get
    /// today's envelope verbatim, forever.</summary>
    public List<ContextSource> Context { get; init; } = [];
}

/// <summary>One <c>context[n]</c> row of a route's declared context block.
/// Exactly ONE source key per row (validated at config load,
/// <c>context-key-unknown</c> for anything else — the additionalProperties
/// law is the validator's, not the binder's):</summary>
/// <example>
/// <code>
/// "context": [
///   { "envelope": "required" },
///   { "history": { "thread": "$event.reference", "last": 20, "order": "asc" } },
///   { "media": { "from": "$event.data.sourceRefs", "content": "brokered", "maxBytes": 8388608 } },
///   { "work": { "fields": ["objective", "state", "requirements"] } },
///   { "timeline": { "last": 30 } },
///   { "memory": { "recall": "$project.tags", "scopes": ["team:$work.team"], "limit": 6 } },
///   { "budget": { "totalBytes": 32768, "onOverflow": "truncate-note" } }
/// ]
/// </code>
/// </example>
public sealed class ContextSource
{
    /// <summary>Envelope source: <c>required</c> (refuse when absent) or
    /// <c>optional</c>; the dispatch envelope's event/mention payload rides
    /// the document.</summary>
    public string? Envelope { get; init; }

    /// <summary>Thread history: <c>thread</c> names the thread (a
    /// <c>$event.reference</c> template or a literal <c>thread://</c> URI),
    /// <c>last</c> caps messages (≤ 100), <c>order</c> asc|desc.</summary>
    public ContextHistory? History { get; init; }

    /// <summary>Inbound media: <c>from</c> a <c>$…sourceRefs</c> template,
    /// content mode (brokered URL today), per-item <c>maxBytes</c>.</summary>
    public ContextMedia? Media { get; init; }

    /// <summary>Work fields: the named subset of the work row
    /// (<c>objective</c>, <c>state</c>, <c>requirements</c>, …).</summary>
    public ContextWork? Work { get; init; }

    /// <summary>Work timeline: the last <c>last</c> (≤ 100) work events.</summary>
    public ContextTimeline? Timeline { get; init; }

    /// <summary>Memory recall: <c>recall</c> is the query (a <c>$…</c>
    /// template), <c>scopes</c> selector list (team-scoped or composition
    /// fails), <c>limit</c> entries.</summary>
    public ContextMemory? Memory { get; init; }

    /// <summary>The whole document's byte budget + overflow behavior
    /// (<c>truncate-note</c> today: trim items and say so).</summary>
    public ContextBudget? Budget { get; init; }

    /// <summary>The <c>context[n]</c> index paths this row populates (for
    /// audit justification strings).</summary>
    public string Key() =>
        Envelope is not null ? "envelope"
        : History is not null ? "history"
        : Media is not null ? "media"
        : Work is not null ? "work"
        : Timeline is not null ? "timeline"
        : Memory is not null ? "memory"
        : Budget is not null ? "budget"
        : string.Empty;
}

public sealed class ContextHistory
{
    public string? Thread { get; init; }
    public int Last { get; init; } = 20;
    public string? Order { get; init; }
}

public sealed class ContextMedia
{
    public string? From { get; init; }
    public string Content { get; init; } = "brokered";
    public long MaxBytes { get; init; } = 8 * 1024 * 1024;
}

public sealed class ContextWork
{
    public List<string> Fields { get; init; } = [];
}

public sealed class ContextTimeline
{
    public int Last { get; init; } = 30;
}

public sealed class ContextMemory
{
    public string? Recall { get; init; }
    public List<string> Scopes { get; init; } = [];
    public int Limit { get; init; } = 6;
}

public sealed class ContextBudget
{
    public int TotalBytes { get; init; } = 32768;
    public string OnOverflow { get; init; } = "truncate-note";
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

            ValidateContextBlocks(section, options);
            options.Provider = section.Key.ToLowerInvariant();
            registry[options.Provider] = options;
        }

        return new ProviderRegistry(registry);
    }

    /// <summary>The closed <c>context[n]</c> key vocabulary — anything else
    /// fails composition (the additionalProperties law, enforced here because
    /// the options binder ignores what it cannot bind).</summary>
    private static readonly string[] ContextKeys =
        ["envelope", "history", "media", "work", "timeline", "memory", "budget"];

    /// <summary>Fail-closed per-section validation of declared context
    /// blocks (ProviderRegistry precedent): unknown keys, a memory scope
    /// selector naming a team outside the route's team, budgets past the
    /// 1 KB–256 KB band or windows past 100 rows. The composition error names
    /// the section and index like the existing addressing failures.</summary>
    private static void ValidateContextBlocks(IConfigurationSection section, ProviderOptions options)
    {
        Fail("AssignContext", "AssignContext", options.AssignContext);
        for (var rule = 0; rule < options.AssignRules.Count; rule++)
        {
            Fail($"AssignRules:{rule}:Context", $"AssignRules[{rule}].Context", options.AssignRules[rule].Context);
        }

        void Fail(string configPath, string displayName, List<ContextSource> block)
        {
            for (var i = 0; i < block.Count; i++)
            {
                var row = block[i];
                var where = $"Providers:{section.Key}:{displayName}[{i}]";
                var raw = section.GetSection($"{configPath}:{i}");
                foreach (var child in raw.GetChildren())
                {
                    if (!ContextKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"{where}: context-key-unknown '{child.Key}' — allowed: {string.Join(", ", ContextKeys)}.");
                    }
                }

                var key = row.Key();
                if (key.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"{where}: context-key-unknown — a context row names exactly one source key.");
                }

                if (row.History is { Last: > 100 } || row.Timeline is { Last: > 100 })
                {
                    throw new InvalidOperationException(
                        $"{where}: context-budget-exceeded — history/timeline windows cap at 100.");
                }

                if (row.Budget is { } budget && (budget.TotalBytes < 1024 || budget.TotalBytes > 262144))
                {
                    throw new InvalidOperationException(
                        $"{where}: context-budget-exceeded — totalBytes must sit within 1 KB–256 KB.");
                }

                if (row.Memory is { } memory)
                {
                    var ruleTeam = displayName.Contains("AssignRules[")
                        ? options.AssignRules[int.Parse(
                            displayName[(displayName.IndexOf('[') + 1)..displayName.IndexOf(']')],
                            System.Globalization.CultureInfo.InvariantCulture)].Team
                        : null;
                    var owningTeam = ruleTeam ?? options.Tenant;
                    foreach (var selector in memory.Scopes)
                    {
                        var scopeTeam = selector.StartsWith("team:", StringComparison.Ordinal)
                            ? selector["team:".Length..]
                            : null;
                        if (scopeTeam is not null && !selector.StartsWith("team:$", StringComparison.Ordinal)
                            && !string.Equals(scopeTeam, owningTeam, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"{where}: memory-scope-outside-team '{selector}' (route team '{owningTeam}').");
                        }
                    }
                }
            }
        }
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

    /// <summary>A formal change-request review verdict (approved /
    /// changes-requested): a decision on the work item, not a comment.</summary>
    public const string Review = Prefix + "review.v1";

    /// <summary>The closed set of central domain types the kernel acts on.</summary>
    static readonly string[] centralTypes = [Message, Mention, Reaction, Issue, ChangeRequest, Pipeline, Review];

    /// <summary>True when the type is one of the seven central domain types.</summary>
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
