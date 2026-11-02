using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Admin;

/// <summary>
/// Spec §17.4: the DB-backed admin config surface — closed
/// validation registry, true If-Match optimistic revision, same-tx
/// history + audit, bounded newest-20 retention. Stored intent only: the
/// suite proves the write ledger, not a runtime read-through.
/// Joins <c>postgres:persistence</c>: schema-bound rows on the shared DB.
/// </summary>
[Collection("postgres:persistence")]
public sealed class AdminConfigStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _client = _factory.CreateAuthedClient();
        // Clean slate for every resource this suite touches (the shared DB
        // accumulates audit + rows across tests otherwise).
        foreach (var resource in new[] { "retry-policy", "routing-defaults", "workload-scale" })
        {
            await fixture.Db.AdminConfigPurge(resource);
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ListReportsEveryRegisteredResourceWithExistence()
    {
        var list = await KernelOps.GetJsonAsync(_client, "/api/admin/config", HttpStatusCode.OK);
        var resources = list.EnumerateArray()
            .ToDictionary(r => r.GetProperty("resource").GetString()!, r => r);
        Assert.Equal(
            new HashSet<string>(new[] { "notifications", "retry-policy", "nudge-policy", "workload-scale", "routing-defaults" }, StringComparer.Ordinal),
            resources.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.False(resources["retry-policy"].GetProperty("exists").GetBoolean(), "purged before the test run");
        Assert.Equal(0, resources["retry-policy"].GetProperty("rowVersion").GetInt64());
    }

    [Fact]
    public async Task FirstWriteIsForcedByVersionOneAndGetSETag()
    {
        var response = await PatchAsync("retry-policy", """{"maxAttempts": 4, "backoffCapSeconds": 600}""", "*");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, body.GetProperty("rowVersion").GetInt64());
        Assert.Equal("\"1\"", response.Headers.ETag!.ToString());
        Assert.Equal("""{"maxAttempts":4,"backoffCapSeconds":600}""", body.GetProperty("payload").GetRawText());

        var stored = await fixture.Db.AdminConfigRow("retry-policy");
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.Value.RowVersion);

        // The fence is real: a PATCH claiming to have seen version 1 lands as version 2.
        var second = await PatchAsync("retry-policy", """{"maxAttempts": 3, "backoffCapSeconds": 600}""", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("rowVersion").GetInt64());
    }

    [Fact]
    public async Task StaleFenceGets412WithCurrentVersionAndWritesNothing()
    {
        await PatchAsync("retry-policy", """{"maxAttempts": 4, "backoffCapSeconds": 600}""", "*");
        var before = await fixture.Db.AdminConfigRow("retry-policy");

        var stale = await PatchAsync("retry-policy", """{"maxAttempts": 9, "backoffCapSeconds": 600}""", "\"777\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith("config-version-conflict", problem.GetProperty("type").GetString());
        Assert.Equal(1, problem.GetProperty("currentVersion").GetInt64());
        Assert.Equal(before, await fixture.Db.AdminConfigRow("retry-policy"));
    }

    [Fact]
    public async Task MissingFenceGets428AndGarbageBodyGets400()
    {
        var noFence = await PatchAsync("retry-policy", """{"maxAttempts": 4, "backoffCapSeconds": 600}""", ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, noFence.StatusCode);
        Assert.Contains("config-precondition-missing", await noFence.Content.ReadAsStringAsync());

        var garbage = await PatchAsync("retry-policy", "not json", "*");
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Contains("config-invalid", await garbage.Content.ReadAsStringAsync());

        Assert.True(await fixture.Db.AdminConfigRow("retry-policy") is null, "no write path ran");
    }

    [Fact]
    public async Task ValidationRegistryRejectsUnknownFieldsRangesAndCrossFieldOrder()
    {
        var unknown = await PatchAsync("retry-policy", """{"maxAttempts": 4, "backoffCapSeconds": 600, "turbo": true}""", "*");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown field 'turbo'", await unknown.Content.ReadAsStringAsync());

        var outOfRange = await PatchAsync("retry-policy", """{"maxAttempts": 99, "backoffCapSeconds": 600}""", "*");
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        Assert.Contains("[1..20]", await outOfRange.Content.ReadAsStringAsync());

        var invertedScale = await PatchAsync("workload-scale",
            """{"minWorkers": 4, "maxWorkers": 2, "idleShutdownMinutes": 30}""", "*");
        Assert.Equal(HttpStatusCode.BadRequest, invertedScale.StatusCode);
        Assert.Contains("minWorkers", await invertedScale.Content.ReadAsStringAsync());

        Assert.True(await fixture.Db.AdminConfigRow("retry-policy") is null, "a rejected write stores nothing");
        Assert.Empty(await fixture.Db.AdminConfigHistoryVersions("retry-policy"));
    }

    [Fact]
    public async Task OversizedPayloadGets400()
    {
        var fat = JsonSerializer.Serialize(new { provider = new string('p', 20_000) });
        var response = await PatchAsync("routing-defaults", fat, "*");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("config-payload-too-large", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownResourceGets404OnReadAndWrite()
    {
        var read = await _client.GetAsync("/api/admin/config/gravity");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Contains("config-resource-unknown", await read.Content.ReadAsStringAsync());

        var write = await PatchAsync("gravity", """{"minWorkers": 1, "maxWorkers": 2, "idleShutdownMinutes": 30}""", "*");
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task NeverWrittenResourceGetsConfigNotSet404()
    {
        var read = await _client.GetAsync("/api/admin/config/nudge-policy");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Contains("config-not-set", await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HistoryTracksEveryVersionNewestFirstAndCapsAtTwenty()
    {
        var trailToken = $"hist-{Guid.NewGuid():N}";
        for (var attempts = 1; attempts <= 22; attempts++)
        {
            var response = await PatchAsync("retry-policy",
                $$"""{"maxAttempts": {{Math.Min(attempts, 20)}}, "backoffCapSeconds": 600}""",
                attempts == 1 ? "*" : $"\"{attempts - 1}\"", reason: trailToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var page = await KernelOps.GetJsonAsync(_client, "/api/admin/config/retry-policy/history?limit=20", HttpStatusCode.OK);
        var versions = page.EnumerateArray().Select(h => h.GetProperty("version").GetInt64()).ToArray();
        Assert.Equal(20, versions.Length);
        Assert.Equal(versions.OrderByDescending(v => v).ToArray(), versions);
        Assert.Equal(22, versions[0]);
        Assert.DoesNotContain(2L, versions);

        // Bounded retention: the table itself keeps only the newest 20 rows.
        var stored = await fixture.Db.AdminConfigHistoryVersions("retry-policy");
        Assert.Equal(Enumerable.Range(3, 20).Select(v => (long)v), stored);

        var audit = (await fixture.Db.AuditRows(decision: "allow"))
            .Where(a => a.Action == "admin.config.patch" && a.Detail!.Contains(trailToken)).ToArray();
        Assert.Equal(22, audit.Length);
        Assert.StartsWith("oidc:", audit[0].Actor);
    }

    [Fact]
    public async Task MissingChangeReasonHeaderGets400()
    {
        var response = await PatchAsync("retry-policy", """{"maxAttempts": 4, "backoffCapSeconds": 600}""", "*", dropReason: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("X-Change-Reason", await response.Content.ReadAsStringAsync());
        Assert.Null(await fixture.Db.AdminConfigRow("retry-policy"));
    }

    private async Task<HttpResponseMessage> PatchAsync(
        string resource, string body, string? ifMatch, bool dropReason = false, string reason = "suite write")
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/config/{resource}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(ifMatch == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue(ifMatch));
        }
        if (!dropReason)
        {
            request.Headers.Add("X-Change-Reason", reason);
        }

        return await _client.SendAsync(request);
    }
}
