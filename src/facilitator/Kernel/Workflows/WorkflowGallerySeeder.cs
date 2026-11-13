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
    ILogger<WorkflowGallerySeeder> logger) : IHostedService
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
