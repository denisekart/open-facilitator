using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Work;

namespace Facilitator.Tests.Work;

/// <summary>
/// The <c>context.fetch</c> plane on the link surface (plan wave 3): a
/// harness posts <c>command.context.fetch.v1</c> and the kernel answers
/// INLINE on the admission ack — an <c>artifact://&lt;stored-id&gt;</c> ref
/// carries the brokered URL + digest and NO bytes (envelope-bounded law,
/// team-scoped); a <c>thread://</c> ref parks as <c>pending</c> and routes a
/// fetch command to the source adapter's endpoint outbox; the adapter's
/// <c>event.context.fetched.v1</c> answer stores a <c>context.history</c>
/// artifact (1d default retention), audits the answer verbatim, and wakes
/// the parked routing intent. Unknown schemes answer an honest error item,
/// never a failed command. Rides the dispatch-link container: the wake law
/// is only observable where no foreign dispatcher can claim these rows.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ContextFetchTests(DispatchDbFixture fixture) : IAsyncLifetime
{
    private const string ThreadUri = "thread://telegram/123/456?limit=20";

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Leaf() => Guid.NewGuid().ToString("N")[..10];

    private static string FetchEnvelope(string workId, string contextRefsJson, string commandId) =>
        $$"""
        {
          "specversion": "1.0",
          "id": "ctx-fetch-{{commandId}}",
          "source": "urn:facilitator:instance:main",
          "type": "org.facilitator.command.context.fetch.v1",
          "subject": "work/{{workId}}",
          "time": "2026-11-15T18:00:00Z",
          "datacontenttype": "application/json",
          "dataschema": "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
          "data": {
            "commandId": "{{commandId}}",
            "workId": "{{workId}}",
            "targetId": "workspace-ws-01",
            "actorEvidenceRef": "identity-evidence:ctx-fetch",
            "deadline": "2026-11-16T18:00:00Z",
            "contextRefs": {{contextRefsJson}}
          }
        }
        """;

    /// <summary>Post one fetch command over the link HTTP plane and return
    /// the ack's in-band <c>data</c> object (the inline answer).</summary>
    private static async Task<JsonElement> PostFetchAsync(
        HttpClient link, string workId, string contextRefsJson, string commandId)
    {
        var ack = await KernelOps.PostJsonAsync(
            link, "/api/link/v1/events", FetchEnvelope(workId, contextRefsJson, commandId),
            HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());
        return ack.GetProperty("data");
    }

    [Fact]
    public async Task ArtifactRefAnswersBrokeredUrlAndDigestWithNoBytes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true,
            settings: new Dictionary<string, string?> { ["OFAC:PublicBaseUrl"] = "https://kernel.example" });
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-cfx-" + Leaf();
        using var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var workId = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");
        var artifactId = Guid.NewGuid();
        await fixture.Db.SeedStoredArtifact(artifactId, "image/png", 40960, workId, "team-alpha", endpoint);

        var data = await PostFetchAsync(link, workId.ToString(),
            $$"""["artifact://work/assignment/{{artifactId:N}}"]""", $"ctxf-{Leaf()}");

        Assert.Empty(data.GetProperty("pending").EnumerateArray());
        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal($"artifact://work/assignment/{artifactId:N}", item.GetProperty("uri").GetString());
        Assert.False(item.GetProperty("truncated").GetBoolean());
        var content = item.GetProperty("content");
        Assert.Equal(artifactId.ToString("N"), content.GetProperty("artifactId").GetString());
        Assert.Equal("image/png", content.GetProperty("mediaType").GetString());
        Assert.Equal("sha256:" + new string('a', 64), content.GetProperty("digest").GetString());
        Assert.Equal("stored", content.GetProperty("state").GetString());
        // The brokered URL: the bytes stay in the store, the answer names
        // the URL and never inlines them.
        Assert.Equal(
            "https://kernel.example/api/artifacts/" + artifactId.ToString("N") + "/content",
            content.GetProperty("contentUrl").GetString());
        Assert.DoesNotContain(content.EnumerateObject(), p => p.Name is "bytes" or "base64");
        var answer = data.GetRawText();
        Assert.DoesNotContain("\"bytes\"", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignTeamArtifactAnswersAnHonestErrorItem()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-cfe-" + Leaf();
        using var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var foreignTeam = "cft-" + Leaf();
        await fixture.Db.SeedTeam(foreignTeam);
        var workId = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject(foreignTeam), null, "review", foreignTeam);
        var artifactId = Guid.NewGuid();
        await fixture.Db.SeedStoredArtifact(artifactId, "image/png", 11, workId, foreignTeam, endpoint);

        // Enrolled under team-alpha; the artifact belongs to another team:
        // the read says what it could not read, the command still stands.
        var data = await PostFetchAsync(link, workId.ToString(),
            $$"""["artifact://{{artifactId:N}}"]""", $"ctxe-{Leaf()}");

        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("artifact-not-readable", item.GetProperty("content").GetProperty("error").GetString());
        Assert.False(item.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task UnknownSchemeAnswersErrorItemNotAFailedCommand()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-cfu-" + Leaf();
        using var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var workId = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");

        var data = await PostFetchAsync(link, workId.ToString(), """["ftp://example/file"]""", $"ctxu-{Leaf()}");

        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("scheme-unsupported", item.GetProperty("content").GetProperty("error").GetString());
    }

    [Fact]
    public async Task ThreadRefParksPendingAndRoutesTheReadToTheSourceAdapter()
    {
        var endpoint = "gw-cft-" + Leaf();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true,
            settings: new Dictionary<string, string?>
            {
                ["Providers:Telegram:Enabled"] = "true",
                ["Providers:Telegram:EndpointId"] = endpoint,
                ["Providers:Telegram:Tenant"] = "demo-team",
            });
        using var kernel = factory.CreateAuthedClient();
        using var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var workId = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");

        var data = await PostFetchAsync(link, workId.ToString(), $$"""["{{ThreadUri}}"]""", $"ctxp-{Leaf()}");

        Assert.Empty(data.GetProperty("items").EnumerateArray());
        var pending = Assert.Single(data.GetProperty("pending").EnumerateArray());
        Assert.Equal(ThreadUri, pending.GetProperty("uri").GetString());
        Assert.Equal("telegram", pending.GetProperty("provider").GetString());
        var fetchId = pending.GetProperty("fetchId").GetString();
        Assert.False(string.IsNullOrEmpty(fetchId));

        // The read rides the source adapter's outbox with the deterministic
        // command id `context-fetch:<fetchId>:<uri-hash>` (ledger-idempotent).
        var routed = (await fixture.Db.OutboxEnvelopes(workId))
            .Where(e => e.Contains("command.context.fetch.v1", StringComparison.Ordinal)
                        && e.Contains(fetchId!, StringComparison.Ordinal))
            .ToList();
        Assert.Single(routed);
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));
    }

    [Fact]
    public async Task FetchedAnswerStoresHistoryArtifactAuditsAndWakesTheIntent()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-cfa-" + Leaf();
        using var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var workId = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");

        // Park a routing intent exactly as the context-pending path leaves
        // it: a routing decision row pending with the default work-assign
        // intent id + an outbox intent row held back on a future availability.
        var intentCommandId = $"routing:work-assign:{workId:N}";
        await fixture.Db.SeedRoutingDecision(intentCommandId, workId, "team-alpha", "context-pending");
        var parked = await fixture.Db.SeedOutboxRow(
            workId, $"link.{endpoint}", RoutingIntentType, intentCommandId, """{"data":{}}""");
        await fixture.Db.DelayOutboxAvailability(parked, TimeSpan.FromMinutes(10));

        // The adapter's asynchronous answer (the frozen fixture rewired to
        // this work): stored as a context.history artifact, audited verbatim,
        // and it pulls the parked intent forward.
        var answer = File.ReadAllText(Path.Combine(PostgresFixture.FixtureRoot(), "valid", "event.context.fetched.json"))
            .Replace("\"work-123\"", $"\"{workId}\"")
            .Replace("\"work/work-123\"", $"\"work/{workId}\"");
        var ack = await KernelOps.PostJsonAsync(
            link, "/api/link/v1/events", answer, HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());

        // The item set is stored as a context.history artifact: born
        // stored+verified, 1d retention (the cheap-to-lose default).
        var history = Assert.Single(await fixture.Db.ArtifactRowsForKind(workId, "context.history"));
        Assert.Equal("stored", history.State);
        Assert.Equal("1d", history.RetentionPolicy);

        // The answer is durable evidence, verbatim.
        var audit = (await fixture.Db.AuditRowsFor("context.fetched"))
            .Single(a => a.Resource == $"work/{workId}");
        Assert.Contains("thread://telegram/123/456", audit.Detail, StringComparison.Ordinal);

        // The wake law: the parked intent's availability was pulled forward.
        Assert.True(await fixture.Db.OutboxAvailableAtOrBefore(parked, DateTime.UtcNow),
            "the fetched answer wakes the parked routing intent");
    }

    private const string RoutingIntentType = "org.facilitator.event.routing.requested.v1";
}
