using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace Facilitator.Tests.Integrations;

/// <summary>
/// A steer on a provider-created Work reaches the owning
/// gateway. A live execution attempt (seeded assignment + attempt over the
/// label-routed assign) ⇒ a schema-shaped <c>work.steer.v1</c> outbox
/// row to the ORIGINAL assign's endpoint. No live execution (§23.1 restart-
/// with-context) ⇒ a <c>work-redispatch:&lt;steerCommandId&gt;</c> assign the
/// funnel emits from a routing intent reusing the original capability/target/
/// inputs with a fresh assignmentId; replay of the steer command is deduped
/// upstream so one re-dispatch lands. A REST-created Work (no assign row)
/// keeps today's timeline-only record. The funnel routes asynchronously over
/// a shared database, so suites seed a live coding fleet and poll drains
/// until the assign/redispatch effect lands.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class SteerDeliveryTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Repo = "demo/widgets";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await fixture.Db.PurgeFleets("gw-coding-");

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task LiveAttempt_SteerRidesOriginalAssignEndpoint()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, client, factory) = await PlaneWithCodingRuleAsync(codingEndpoint);
        await FleetAsync(codingEndpoint);
        var workId = await CreateCodingWorkAsync(link, factory);

        var assignmentId = await EffectAssignmentIdAsync(workId);
        await fixture.Db.SeedExecutionAttempt(assignmentId, "working");

        var steerCommand = "steer-live-" + Guid.NewGuid().ToString("N");
        var steered = await client.PostAsync(
            $"/api/works/{workId}/steer",
            Content($$"""{"directive":"rename the helper","commandId":"{{steerCommand}}","expectedVersion":1}"""));
        Assert.Equal(HttpStatusCode.OK, steered.StatusCode);

        var envelope = await fixture.Db.OutboxEnvelopeByCommand($"work-steer:{steerCommand}");
        Assert.NotNull(envelope);
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("org.facilitator.command.work.steer.v1", doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(workId.ToString(), data.GetProperty("workId").GetString());
        Assert.Equal("rename the helper", data.GetProperty("directive").GetString());
        Assert.Equal($"work-steer:{steerCommand}", data.GetProperty("commandId").GetString());
        // The gateway's binding row keys on the ASSIGN envelope's wire
        // assignmentId, so the steer must carry it, not the DB row identity.
        var original = await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId:N}");
        Assert.NotNull(original);
        Assert.Equal(
            JsonDocument.Parse(original).RootElement.GetProperty("data").GetProperty("assignmentId").GetString(),
            data.GetProperty("assignmentId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId);
        Assert.Contains($"link.{codingEndpoint}", recipients);
    }

    [Fact]
    public async Task NoLiveAttempt_SteerRedispatchesOriginalAssignment()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, client, factory) = await PlaneWithCodingRuleAsync(codingEndpoint);
        await FleetAsync(codingEndpoint);
        var workId = await CreateCodingWorkAsync(link, factory);

        var steerCommand = "steer-rd-" + Guid.NewGuid().ToString("N");
        string Body() =>
            $$"""{"directive":"address review","commandId":"{{steerCommand}}","expectedVersion":1}""";
        var steered = await client.PostAsync($"/api/works/{workId}/steer", Content(Body()));
        Assert.Equal(HttpStatusCode.OK, steered.StatusCode);
        // Replay dedupes in the inbox: exactly one re-dispatch rides the lane.
        var replayed = await client.PostAsync($"/api/works/{workId}/steer", Content(Body()));
        Assert.Equal("duplicate", (await JsonWire.ReadJsonAsync(replayed.Content)).GetProperty("status").GetString());

        // The re-dispatch rides the funnel: the steer appends a routing intent
        // (preference = the original recipient), the funnel emits the effect.
        var envelope = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-redispatch:{steerCommand}"),
            "re-dispatch assign effect");
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("org.facilitator.command.work.assign.v1", doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("coding.implement", data.GetProperty("capabilityId").GetString());
        Assert.Equal("demo/widgets", data.GetProperty("inputs").GetProperty("repo").GetString());
        var original = await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId:N}");
        Assert.NotNull(original);
        Assert.NotEqual(
            JsonDocument.Parse(original).RootElement.GetProperty("data").GetProperty("assignmentId").GetString(),
            data.GetProperty("assignmentId").GetString());
        Assert.Equal($"work/{workId}/assignment/{data.GetProperty("assignmentId").GetString()}",
            doc.RootElement.GetProperty("subject").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId);
        Assert.Contains($"link.{codingEndpoint}", recipients);
    }

    [Fact]
    public async Task TerminalAttempt_SteerRedispatchesToo()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, client, factory) = await PlaneWithCodingRuleAsync(codingEndpoint);
        await FleetAsync(codingEndpoint);
        var workId = await CreateCodingWorkAsync(link, factory);
        var assignmentId = await EffectAssignmentIdAsync(workId);
        await fixture.Db.SeedExecutionAttempt(assignmentId, "stopped");

        var steerCommand = "steer-term-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(
            $"/api/works/{workId}/steer",
            Content($$"""{"directive":"one more round","commandId":"{{steerCommand}}","expectedVersion":1}"""))).StatusCode);
        Assert.NotNull(await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-redispatch:{steerCommand}"),
            "terminal re-dispatch assign effect"));
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-steer:{steerCommand}"));
    }

    [Fact]
    public async Task RestWorkWithoutAssign_SteerStaysTimelineOnly()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (_, client, _) = await PlaneWithCodingRuleAsync(codingEndpoint);
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "no provider lineage");

        var steerCommand = "steer-rest-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(
            $"/api/works/{workId}/steer",
            Content($$"""{"directive":"quiet","commandId":"{{steerCommand}}","expectedVersion":1}"""))).StatusCode);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-steer:{steerCommand}"));
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-redispatch:{steerCommand}"));
        Assert.Equal(["kernel.log"], await fixture.Db.OutboxRecipients(workId));
    }

    // ---- plane ------------------------------------------------------------

    private async Task<(HttpClient Link, HttpClient Authed, KernelTestFactory Factory)> PlaneWithCodingRuleAsync(string codingEndpointId)
    {
        var endpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
            ["Providers:Forgejo:AssignEndpointId"] = "gw-global-unused",
            ["Providers:Forgejo:AssignRules:0:Label"] = "ofac:coding",
            ["Providers:Forgejo:AssignRules:0:CapabilityId"] = "coding.implement",
            ["Providers:Forgejo:AssignRules:0:EndpointId"] = codingEndpointId,
        });
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (link, factory.CreateAuthedClient(), factory);
    }

    /// <summary>A live coding.implement fleet on team-alpha so the funnel has
    /// the rule endpoint as a candidate.</summary>
    private Task FleetAsync(string codingEndpoint) =>
        FleetHarness.SeedFleetAsync(fixture.Db, codingEndpoint, "team-alpha", "coding.implement");

    /// <summary>A labeled issue event arms a Work whose interim assign rides
    /// the coding rule lane (24(1a)); the funnel's effect row is awaited, so
    /// callers can read the assign envelope back immediately. Returns the
    /// armed Work's id.</summary>
    private async Task<Guid> CreateCodingWorkAsync(HttpClient link, KernelTestFactory factory)
    {
        var issue = new Random().Next(10000, 99999).ToString();
        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            $"dlv-{Guid.NewGuid():N}", Repo, issue, "alice", label: "ofac:coding")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);
        await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"),
            "coding assign effect");
        return workId.Value;
    }

    /// <summary>The funnel's routed assignment row for the work (the effect
    /// minted it alongside the assign envelope); an attempt rides it.</summary>
    private async Task<Guid> EffectAssignmentIdAsync(Guid workId) =>
        Guid.Parse(Assert.Single(await fixture.Db.AssignmentIdsForWork(workId)));

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The forgejo labeled-issue fixture with a fresh delivery/issue
    /// identity and the given label present in the verbatim provider payload.</summary>
    private static string IssueEvent(string deliveryId, string repo, string number, string actor, string label)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.labeled.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        reference["authorProviderAccountId"] = actor;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["label"] = new JsonObject { ["name"] = label };
        payload["issue"] = new JsonObject
        {
            ["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
            ["labels"] = new JsonArray(new JsonObject { ["name"] = label }),
        };
        return doc.ToJsonString();
    }
}
