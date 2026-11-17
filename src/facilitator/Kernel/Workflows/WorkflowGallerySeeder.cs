using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// Gallery seeding: the eight built-in definitions ship as embedded resources
/// (canonical JSON) and land at boot as <c>source='gallery'</c>,
/// <c>enabled=false</c> rows — <b>missing rows only, operator edits are never
/// overwritten</b> (the TeamRosterSeeder posture: config seeds, DB is truth
/// thereafter). Enabling stays an operator act (audited toggle), so shipping a
/// gallery entry never changes behavior.
/// </summary>
public sealed class WorkflowGallerySeeder(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<WorkflowGallerySeeder> logger,
    Facilitator.Kernel.Integrations.ProviderRegistry providers,
    WorkflowSubscriptionRegistry subscriptions) : IHostedService
{
    /// <summary>The embedded gallery documents (logical name = filename).</summary>
    public static IReadOnlyDictionary<string, string> Gallery { get; } = Load();

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same gate as DatabaseInitializer/TeamRosterSeeder: with
        // AutoMigrate=false the schema is not ours to touch (health-only
        // tests boot without a database).
        if (configuration["Database:AutoMigrate"] == "false")
        {
            logger.LogInformation("Database:AutoMigrate=false — skipping workflow gallery seeding");
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var seeded = 0;
        foreach (var (name, json) in Gallery)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var defId = root.GetProperty("id").GetString()!;
            var version = root.GetProperty("version").GetInt32();

            // Idempotent raw insert (ON CONFLICT DO NOTHING on the PK — the
            // TeamRosterSeeder law): replicas boot in parallel, a human or
            // operator edit always wins over the seed, and a second boot
            // changes nothing.
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO facilitator.workflow_definitions
                     (def_id, version, title, description, payload, digest, enabled, shadow, source, team, created_at, created_by)
                 VALUES
                     ({defId}, {version}, {root.GetProperty("title").GetString()!}, {root.GetProperty("description").GetString()!},
                      {WorkflowDoc.Canonical(root)}::jsonb, {WorkflowDoc.Digest(root)}, false, false, 'gallery',
                      {(root.TryGetProperty("team", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)},
                      {timeProvider.GetUtcNow().UtcDateTime}, 'gallery')
                 ON CONFLICT (def_id, version) DO NOTHING
                 """, cancellationToken);
            seeded += affected;
            _ = name;
        }

        if (seeded > 0)
        {
            logger.LogInformation("workflow gallery seeded: {Count} definitions (enabled=false until an operator says so)", seeded);
        }

        await ImportPrRequestScopesAsync(db, cancellationToken);
        await ImportAssignRulesAsync(db, cancellationToken);
    }

    /// <summary>
    /// The assign-rules importer (Phase 5): each
    /// <c>Providers:&lt;p&gt;:AssignRules[n]</c> config row imports (if
    /// absent) an ENABLED scoped copy of the <c>assign-rules</c> gallery
    /// template — defId <c>assign-rule-&lt;p&gt;-&lt;n&gt;</c> (n is the
    /// 1-based config order), source='derived' — with the rule baked into
    /// the create step: capability, endpoint preference, target, owning
    /// team, and a <c>data.labels</c> matcher term for the rule's label.
    /// An imported live copy is exactly what the labeled-issue arm defers
    /// to (<c>PlaneOwnedAssignWorkAsync</c>): the arm's own
    /// <c>MatchAssignRule</c> path stops performing the creation the plane
    /// owns. Deleting the config rule leaves the definition (the operator
    /// owns it now — the "never removes a seam" law); with the plane
    /// disabled the arm runs exactly as today. Corner law: an issue whose
    /// labels hit TWO of one provider's rules fires two plane flows (the
    /// arm's law was first-match-wins) — an honest, visible difference an
    /// operator resolves by disambiguating labels or disabling a copy.
    /// </summary>
    private async Task ImportAssignRulesAsync(FacilitatorDbContext db, CancellationToken cancellationToken)
    {
        var template = JsonNode.Parse(Gallery[AssignRulesTemplate.DefinitionId])!;
        foreach (var options in providers.All().Where(static o => o.Enabled))
        {
            for (var index = 0; index < options.AssignRules.Count; index++)
            {
                var rule = options.AssignRules[index];
                if (rule.Label is not { Length: > 0 } label)
                {
                    continue;
                }

                var doc = JsonNode.Parse(template.ToJsonString())!.AsObject();
                var defId = AssignRulesTemplate.DefinitionIdFor(options.Provider, index + 1);
                doc["id"] = defId;
                doc["title"] = $"Assign rule {label} ({options.Provider} rule {index + 1})";
                doc["description"] =
                    $"Derived from Providers:{options.Provider}:AssignRules[{index}] (label {label}): "
                    + "create the work under the rule's capability and ack the thread. Baked from config at boot; "
                    + "the row is the operator's from here (disable to hand the behavior back to the arm).";
                var where = doc["on"]!["where"]!.AsObject();
                where["data.labels"] = label.ToLowerInvariant();
                // Provider scope (the arm's law: rules live on ONE provider's
                // options): a forgejo rule must not fire on a telegram issue
                // that happens to carry the same label — without this term
                // the broadcast trigger law fans one labeled event across
                // every provider's same-labeled rule copy.
                where["data.provider"] = options.Provider.ToLowerInvariant();
                var create = doc["steps"]!.AsArray()
                    .Select(st => st!.AsObject())
                    .First(st => st["id"]!.GetValue<string>() == AssignRulesTemplate.CreateStepId);
                var workCreate = create["work-create"]!.AsObject();
                workCreate["capability"] = rule.CapabilityId ?? options.AssignCapabilityId ?? "work.default";
                if (rule.Team is { Length: > 0 } team)
                {
                    workCreate["team"] = team;
                }

                if (rule.EndpointId is { Length: > 0 } endpoint)
                {
                    workCreate["preference"] = endpoint;
                }

                workCreate["target"] = rule.TargetId ?? options.AssignTargetId ?? $"{options.Provider}-interim";
                // Baked at import (the row is the operator's from then on —
                // the "never removes a seam" law): a later config edit
                // changes the ARM's read but not the imported flow; the
                // operator edits the definition to move the flow.



                var canonical = WorkflowDoc.Canonical(JsonDocument.Parse(doc.ToJsonString()).RootElement);
                var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO facilitator.workflow_definitions
                         (def_id, version, title, description, payload, digest, enabled, shadow, source, team, created_at, created_by, next_tick_at)
                     VALUES
                         ({defId}, 1, {doc["title"]!.GetValue<string>()}, {doc["description"]!.GetValue<string>()},
                          {canonical}::jsonb, {WorkflowDoc.Digest(JsonDocument.Parse(canonical).RootElement)},
                          true, false, 'derived', {(rule.Team is { Length: > 0 } ruleTeam ? ruleTeam : null)}, {timeProvider.GetUtcNow().UtcDateTime}, 'importer', NULL)
                     ON CONFLICT (def_id, version) DO NOTHING
                     """, cancellationToken);
                if (affected > 0)
                {
                    logger.LogInformation(
                        "assign rule imported: {Definition} enabled (label {Label}, capability {Capability})",
                        defId, label, workCreate["capability"]!.GetValue<string>());
                    // Same subscription-set law as the PR scopes: an
                    // imported ENABLED row must be visible to the emitter
                    // now, not after the cache TTL.
                    await subscriptions.RebuildAsync(cancellationToken);
                }
            }
        }
    }

    /// <summary>One-release importer (Phase 4): <c>Providers:&lt;p&gt;:PrRequestCapability</c>
    /// is DEPRECATED — it now imports a scoped COPY of pr-on-manifest
    /// (defId <c>pr-on-manifest-&lt;p&gt;</c>, source='derived', ENABLED):
    /// the generic gallery shape + a first guard step on
    /// <c>assign.capability</c> (the completing assignment's capability —
    /// what the option selected on) + the binding provider token baked in.
    /// Missing rows only: once imported, the copy is operator data (the
    /// TeamRosterSeeder posture). Delete the importer one release after
    /// the swap ships.</summary>
    private async Task ImportPrRequestScopesAsync(
        FacilitatorDbContext db, CancellationToken cancellationToken)
    {
        foreach (var options in providers.All()
                     .Where(o => o.Enabled && !string.IsNullOrEmpty(o.PrRequestCapability)))
        {
            var gallery = Gallery.FirstOrDefault(g =>
                g.Key.StartsWith("pr-on-manifest", StringComparison.Ordinal)).Value;
            if (gallery is null)
            {
                return;
            }

            using var parsed = JsonDocument.Parse(gallery);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(parsed.RootElement.GetRawText())!.AsObject();
            doc["id"] = $"pr-on-manifest-{options.Provider.ToLowerInvariant()}";
            doc["description"] = $"Scoped copy of pr-on-manifest for provider '{options.Provider}' "
                                 + $"(imported from Providers:{options.Provider}:PrRequestCapability, "
                                 + "deprecated): only assignments completing with capability "
                                 + $"'{options.PrRequestCapability}' dispatch PR legs.";
            // The where law is scalar-only (matcher terms match scalars):
            // the capability scope rides the guard step below, which reads
            // `assign.capability` (the runner resolves it from the event's
            // assignmentId against the assign envelope row).
            doc!["on"]!["where"] = new System.Text.Json.Nodes.JsonObject
            {
                ["data.resultManifestRef"] = "*",
            };
            doc["steps"]!.AsArray().Insert(0, System.Text.Json.Nodes.JsonNode.Parse(
                """
                {
                  "id": "guard-assign",
                  "title": "Scope: the completing assignment's capability",
                  "on": "always",
                  "decide": {
                    "source": "guard",
                    "guard": { "field": "assign.capability", "equals": "PLACEHOLDER" },
                    "fallback": "end"
                  }
                }
                """)!);
            var guardStep = doc["steps"]![0]!.AsObject();
            guardStep["decide"]!["guard"]!["equals"] = options.PrRequestCapability;
            doc["steps"]![1]!["on"] = "guard-assign:completed";
            doc["steps"]!.AsArray()
                .Select(st => st!.AsObject())
                .First(st => st["id"]!.GetValue<string>() == "pr-create")
                ["emit"]!["binding"]!["provider"] = options.Provider.ToLowerInvariant();

            var defId = doc["id"]!.GetValue<string>();
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO facilitator.workflow_definitions
                     (def_id, version, title, description, payload, digest, enabled, shadow, source, team, created_at, created_by)
                 VALUES
                     ({defId}, 1, {doc["title"]!.GetValue<string>()}, {doc["description"]!.GetValue<string>()},
                      {doc.ToJsonString()}::jsonb, {WorkflowDoc.Digest(JsonDocument.Parse(doc.ToJsonString()).RootElement)},
                      true, false, 'derived', NULL, {timeProvider.GetUtcNow().UtcDateTime}, 'importer')
                 ON CONFLICT (def_id, version) DO NOTHING
                 """, cancellationToken);
            if (affected > 0)
            {
                logger.LogInformation(
                    "pr-request scope imported: {Definition} enabled for capability {Capability} (the PrRequestCapability option is deprecated)",
                    defId, options.PrRequestCapability);
                // An imported ENABLED row extends the subscription set now:
                // the boot-time cache could otherwise miss the type for its
                // whole TTL and silently drop the first triggers (the
                // toggle path rebuilds the same way).
                await subscriptions.RebuildAsync(cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyDictionary<string, string> Load()
    {
        var assembly = typeof(WorkflowGallerySeeder).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(static n => n.EndsWith(".json", StringComparison.Ordinal))
                     .Where(static n =>
                     {
                         // The gallery files are the eight known names; the
                         // protocol schemas embed under the same simple
                         // logical-name regime in Ofac.Protocol, not here.
                         var name = n[..^".json".Length];
                         return name is "review-fork" or "pr-on-manifest" or "assign-rules" or "nudge-escalation"
                                or "ensure-capacity" or "agentic-research" or "router-ingress" or "team-home";
                     }))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            result[resourceName[..^".json".Length]] = reader.ReadToEnd();
        }

        return result;
    }
}
