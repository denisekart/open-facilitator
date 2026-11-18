using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.Tests.Work;

/// <summary>
/// The <c>context.fetch</c> expansion core, called straight on the service:
/// the budget law (≤ 32 refs, ≤ 32 KB serialized per item, honest
/// <c>truncated</c>), the URI-splitting law (memory://team:alpha?q=… rides
/// a hand-rolled split because Uri would choke on the colon authority), the
/// scope law (a <c>team:</c> selector outside the caller's team answers a
/// deny item, missing selector/q answer a needs item), the
/// logical-URI artifact law (GUID = LAST path segment; metadata + brokered
/// content URL, never bytes; foreign-team rows are not readable), and the
/// <c>thread://</c> pending leg. Memory hits ride the real Postgres-FTS
/// broker (deterministic seeded rows — no AI leg anywhere in this plane).
/// </summary>
[Collection("postgres:work")]
public sealed class ContextFetchServiceTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly KernelTestFactory _factory = new(fixture, noDispatcher: true);
    private readonly List<string> _teamSlugs = [];
    private readonly List<Guid> _artifactIds = [];
    private readonly List<Guid> _entryIds = [];
    private readonly List<Guid> _scopeIds = [];
    private readonly Dictionary<string, Guid> _memoryScopes = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ = _factory.CreateAuthedClient(); // boots migrations
        await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE requested_by_endpoint_id = $1",
            cmd => cmd.Parameters.AddWithValue("cfs.endpoint"));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var id in _artifactIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE artifact_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _entryIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.memory_usage WHERE entry_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
            await NonQueryAsync("DELETE FROM facilitator.memory_entries WHERE entry_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _scopeIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.memory_scopes WHERE scope_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var slug in _teamSlugs)
        {
            await NonQueryAsync("DELETE FROM facilitator.teams WHERE slug = $1",
                cmd => cmd.Parameters.AddWithValue(slug));
        }

        _factory.Dispose();
    }

    public void Dispose() => _factory.Dispose();

    private ContextFetchService Service() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ContextFetchService>();

    private async Task<string> SeedTeamAsync()
    {
        var slug = "cfs-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedTeam(slug);
        _teamSlugs.Add(slug);
        return slug;
    }

    private static LinkPrincipal Principal(string team) =>
        new("cfs.endpoint", Guid.NewGuid(), team, "mode-b", "adapter");

    // ---- URI splitting (pure) ------------------------------------------------

    [Theory]
    [InlineData("memory://team:alpha?q=notes#frag", "memory", "team:alpha", "q=notes")]
    [InlineData("thread://telegram/123/456?limit=20", "thread", "telegram/123/456", "limit=20")]
    [InlineData("artifact://work/assign/9f2b71c4-58d1-4f0a-9c63-71a0d5e2b107",
        "artifact", "work/assign/9f2b71c4-58d1-4f0a-9c63-71a0d5e2b107", "")]
    [InlineData("no-scheme-here", "", "no-scheme-here", "")]
    [InlineData("MEMORY://team:alpha?q=x", "memory", "team:alpha", "q=x")] // scheme lower-cased
    public void SplitLaws(string uri, string scheme, string authority, string query)
    {
        var (s, a, q) = ContextFetchService.Split(uri);
        Assert.Equal(scheme, s);
        Assert.Equal(authority, a);
        Assert.Equal(query, q);
    }

    // ---- honest error items ---------------------------------------------------

    [Fact]
    public async Task UnknownSchemeAndOverflowAnswerHonestItems()
    {
        var team = await SeedTeamAsync();
        var refs = Enumerable.Repeat("memory://team:" + team + "?q=x", ContextFetchService.MaxRefs + 4)
            .Append("carrier://pigeon/1")
            .ToList();

        var result = await Service().ExpandAsync(refs, Principal(team), null, default);

        // The contract ceiling: at most 32 refs are expanded — the pigeon
        // never reaches an item, the 32 memory items do.
        Assert.Equal(ContextFetchService.MaxRefs, result.Items.Count);
        Assert.Empty(result.Pending);
        Assert.DoesNotContain(result.Items, i => i.Uri.StartsWith("carrier", StringComparison.Ordinal));

        var pigeon = await Service().ExpandAsync(["carrier://pigeon/1"], Principal(team), null, default);
        var item = Assert.Single(pigeon.Items);
        Assert.Equal("scheme-unsupported", item.Content["error"]!.GetValue<string>());
        Assert.False(item.Truncated);
        Assert.NotEmpty(pigeon.FetchId);
    }

    [Fact]
    public async Task MemorySelectorOutsideTheTeamDenies()
    {
        var team = await SeedTeamAsync();
        var result = await Service().ExpandAsync(
            ["memory://team:not-my-team?q=x"], Principal(team), null, default);
        Assert.Equal("memory-scope-outside-team",
            Assert.Single(result.Items).Content["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("memory://?q=x")] // no selector
    [InlineData("memory://project:nonguid-sel")] // no q (and a selector the scope resolver cannot name)
    public async Task MemoryNeedsSelectorAndQuery(string uri)
    {
        var team = await SeedTeamAsync();
        var result = await Service().ExpandAsync([uri], Principal(team), null, default);
        Assert.Equal("memory-needs-selector-and-q",
            Assert.Single(result.Items).Content["error"]!.GetValue<string>());
    }

    // ---- memory recall legs ----------------------------------------------------

    [Fact]
    public async Task MemoryHitCarriesEntryFieldsAndTheBrokerFlagsItTruncated()
    {
        var team = await SeedTeamAsync();
        var content = "hull-plating inspection log: micro-fracture along plate seven, patch tomorrow";
        var (entryId, _) = await SeedMemoryAsync(team, content);

        var result = await Service().ExpandAsync(
            [$"memory://team:{team}?q=hull-plating"], Principal(team), null, default);
        var item = Assert.Single(result.Items);
        Assert.False(item.Truncated);
        var entries = item.Content["entries"]!.AsArray();
        var entry = Assert.Single(entries)!.AsObject();
        Assert.Equal(entryId.ToString("N"), entry["entryId"]!.GetValue<string>());
        Assert.Contains("hull-plating", entry["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(item.Content["truncatedBySource"]!.GetValue<bool>());
        Assert.Equal(1, await fixture.Db.MemoryUsageCount(entryId, "assignment.context"));
    }

    [Fact]
    public async Task RecallOverTheItemBudgetIsHonestlyTruncated()
    {
        var team = await SeedTeamAsync();
        // Twelve fat entries: past the 32 KB per-item budget the loop stops
        // and says so — never a silent cut.
        for (var i = 0; i < 12; i++)
        {
            var filler = new string('x', 6000);
            var (entryId, _) = await SeedMemoryAsync(team, $"hull-plating note {i} {filler}");
            _ = entryId;
        }

        var result = await Service().ExpandAsync(
            [$"memory://team:{team}?q=hull-plating"], Principal(team), null, default);
        var item = Assert.Single(result.Items);
        Assert.True(item.Truncated);
        Assert.True(item.Content["entries"]!.AsArray().Count < 12);
    }

    // ---- artifact legs ----------------------------------------------------------

    [Fact]
    public async Task ArtifactUriTakesTheLastSegmentAndAnswersMetadataOnly()
    {
        var team = await SeedTeamAsync();
        var artifactId = Guid.NewGuid();
        _artifactIds.Add(artifactId);
        await fixture.Db.SeedStoredArtifact(artifactId, "image/png", 4096, null, team, "cfs.endpoint");

        var result = await Service().ExpandAsync(
            [$"artifact://work/assign/{artifactId}"], Principal(team), null, default);
        var item = Assert.Single(result.Items);
        Assert.Equal(artifactId.ToString("N"), item.Content["artifactId"]!.GetValue<string>());
        Assert.Equal("image/png", item.Content["mediaType"]!.GetValue<string>());
        Assert.Equal("stored", item.Content["state"]!.GetValue<string>());
        Assert.Equal($"/api/artifacts/{artifactId:N}/content",
            item.Content["contentUrl"]!.GetValue<string>()); // no PublicBaseUrl configured
        Assert.Equal(4096, item.Content["size"]!.GetValue<long>());
        Assert.False(item.Truncated);
    }

    [Fact]
    public async Task ArtifactUnparsableGuidAndForeignTeamAnswerHonestItems()
    {
        var team = await SeedTeamAsync();
        var unparseable = await Service().ExpandAsync(
            ["artifact://work/assign/not-a-guid"], Principal(team), null, default);
        Assert.Equal("artifact-guid-unparseable",
            Assert.Single(unparseable.Items).Content["error"]!.GetValue<string>());

        var foreign = Guid.NewGuid();
        _artifactIds.Add(foreign);
        await fixture.Db.SeedStoredArtifact(foreign, "image/png", 10, null, "someone-else", "other.ep");
        var denied = await Service().ExpandAsync(
            [$"artifact://x/{foreign}"], Principal(team), null, default);
        Assert.Equal("artifact-not-readable",
            Assert.Single(denied.Items).Content["error"]!.GetValue<string>());
    }

    // ---- thread pending leg ------------------------------------------------------

    [Fact]
    public async Task ThreadRefsParkAsPendingOnOneFetchId()
    {
        var team = await SeedTeamAsync();
        var result = await Service().ExpandAsync(
            ["thread://telegram/123/456?limit=20", "thread://matrix/room/7"],
            Principal(team), null, default);
        Assert.Empty(result.Items);
        Assert.Equal(2, result.Pending.Count);
        Assert.All(result.Pending, p => Assert.Equal(result.FetchId, p.FetchId));
        Assert.Equal("telegram", result.Pending[0].Provider); // authority's first segment
        Assert.Equal("matrix", result.Pending[1].Provider);
    }

    // ---- helpers -----------------------------------------------------------------

    private async Task<(Guid EntryId, Guid ScopeId)> SeedMemoryAsync(string teamSlug, string content)
    {
        // One team scope per slug: canonical_selector is unique.
        if (!_memoryScopes.TryGetValue(teamSlug, out var scopeId))
        {
            scopeId = Guid.NewGuid();
            _memoryScopes[teamSlug] = scopeId;
            _scopeIds.Add(scopeId);
            await NonQueryAsync(
                "INSERT INTO facilitator.memory_scopes (scope_id, kind, team_slug, canonical_selector, created_at) "
                + "VALUES ($1, 'team', $2, 'team:' || $2, now())",
                cmd =>
                {
                    cmd.Parameters.AddWithValue(scopeId);
                    cmd.Parameters.AddWithValue(teamSlug);
                });
        }

        var entryId = Guid.NewGuid();
        _entryIds.Add(entryId);
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        await NonQueryAsync(
            "INSERT INTO facilitator.memory_entries (entry_id, scope_id, team_slug, type, content, media_type, "
            + "content_summary, content_hash, provenance, observed_at, classification, policy_label, status, "
            + "revision, contradicts_entry_ids, created_at, created_by) "
            + "VALUES ($1, $2, $3, 'observed_fact', $4, 'text/plain', 'seeded', $5, "
            + "'{\"source\":\"seed\"}', now(), 'internal', 'facilitator-visible', 'active', 1, '{}', now(), 'seed')",
            cmd =>
            {
                cmd.Parameters.AddWithValue(entryId);
                cmd.Parameters.AddWithValue(scopeId);
                cmd.Parameters.AddWithValue(teamSlug);
                cmd.Parameters.AddWithValue(content);
                cmd.Parameters.AddWithValue(hash);
            });
        return (entryId, scopeId);
    }

    private async Task NonQueryAsync(string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync();
    }
}
