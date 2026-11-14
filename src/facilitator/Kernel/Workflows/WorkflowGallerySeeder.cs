using System.Reflection;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// Gallery seeding: the four built-in definitions ship as embedded resources
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
                         // The gallery files are the four known names; the
                         // protocol schemas embed under the same simple
                         // logical-name regime in Ofac.Protocol, not here.
                         var name = n[..^".json".Length];
                         return name is "review-fork" or "pr-on-manifest" or "assign-rules" or "nudge-escalation";
                     }))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            result[resourceName[..^".json".Length]] = reader.ReadToEnd();
        }

        return result;
    }
}
