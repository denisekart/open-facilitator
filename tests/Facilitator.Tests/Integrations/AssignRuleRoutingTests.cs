using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace Facilitator.Tests.Integrations;

/// <summary>
/// Label-routed interim assign as a routing intent: a Work-creating issue
/// event whose verbatim <c>payload</c> carries a rule's label contributes a
/// <c>coding.implement</c> intent keyed <c>work-assign-coding:</c> (rule
/// inputs ride along; the rule endpoint is a PREFERENCE the funnel satisfies
/// when the endpoint is a live candidate). A non-matching label (or no rule)
/// keeps the global <c>AssignEndpointId</c>/<c>AssignCapabilityId</c> pair as
/// the intent, and a rule with a blank endpoint prefers the global endpoint
/// while keeping its capability. Effects land asynchronously (shared-database
/// dispatchers race), so every assertion polls drains via
/// <see cref="RoutingHarness"/>; per-test fleets ride unique endpoint names.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class AssignRuleRoutingTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Repo = "demo/widgets";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // A leaked live fleet would join a later test's candidate set.
        await fixture.Db.PurgeFleets("gw-coding-");
        await fixture.Db.PurgeFleets("gw-global-");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task MatchingLabelRoutesCodingCapabilityToRuleEndpoint()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (link, endpoint, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpoint);
        // The coding fleet is live and advertises the rule capability.
        await FleetAsync(codingEndpoint, "coding.implement");
        await FleetAsync(globalEndpoint, "analysis.summarize");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:coding")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        // Rule routing: coding.implement addressed to link.<rule endpoint>,
        // rule-keyed command id, repo/issue re-hydration inputs.
        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"),
            "coding assign effect");
        using var doc = JsonDocument.Parse(assign);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("coding.implement", data.GetProperty("capabilityId").GetString());
        Assert.Equal("forgejo-interim", data.GetProperty("targetId").GetString());
        Assert.Equal("demo/widgets", data.GetProperty("inputs").GetProperty("repo").GetString());
        Assert.Equal(issue, data.GetProperty("inputs").GetProperty("issue").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{codingEndpoint}", recipients);
        Assert.Contains($"link.{endpoint}", recipients); // ack rides the adapter endpoint
        Assert.DoesNotContain($"link.{globalEndpoint}", recipients);

        // The rule endpoint rode as a preference and was satisfied (the audit
        // commits with the effect, so it is present the moment the effect is).
        var audit = await fixture.Db.RoutingDecision($"routing:work-assign-coding:{workId.Value:N}");
        Assert.NotNull(audit);
        Assert.Equal("preference-satisfied", audit!.Basis);
        Assert.Equal(codingEndpoint, audit.ChosenEndpointId);
    }

    [Fact]
    public async Task NonMatchingLabelKeepsGlobalInterimIntent()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpoint);
        await FleetAsync(globalEndpoint, "analysis.summarize");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:other")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        // 22/23 regression shape: the global pair rides the plain command id.
        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId.Value:N}"),
            "global assign effect");
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal(
            "analysis.summarize",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{globalEndpoint}", recipients);
        Assert.DoesNotContain($"link.{codingEndpoint}", recipients);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"));
    }

    [Fact]
    public async Task RuleWithBlankEndpointPrefersGlobalEndpointKeepingCapability()
    {
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpointId: "");
        // Only the GLOBAL fleet is live and it advertises the rule capability.
        await FleetAsync(globalEndpoint, "coding.implement");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:coding")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"),
            "blank-endpoint coding assign effect");
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal(
            "coding.implement",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{globalEndpoint}", recipients);
        var audit = await fixture.Db.RoutingDecision($"routing:work-assign-coding:{workId.Value:N}");
        Assert.NotNull(audit);
        Assert.Equal("preference-satisfied", audit!.Basis);
        Assert.Equal(globalEndpoint, audit.ChosenEndpointId);
    }

    // ---- plane ------------------------------------------------------------

    private async Task<(HttpClient Link, string AdapterEndpoint, KernelTestFactory Factory)> PlaneWithRulesAsync(
        string globalAssignEndpoint, string codingEndpointId)
    {
        var endpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
            ["Providers:Forgejo:AssignEndpointId"] = globalAssignEndpoint,
            ["Providers:Forgejo:AssignRules:0:Label"] = "ofac:coding",
            ["Providers:Forgejo:AssignRules:0:CapabilityId"] = "coding.implement",
            ["Providers:Forgejo:AssignRules:0:EndpointId"] = codingEndpointId,
        });
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (link, endpoint, factory);
    }

    /// <summary>A live fleet advertising one capability on team-alpha (the
    /// adapter's enrollment team, so the work's team never excludes it).</summary>
    private Task FleetAsync(string endpoint, string capability) =>
        FleetHarness.SeedFleetAsync(fixture.Db, endpoint, "team-alpha", capability);

    private static string NewIssueNumber() =>
        new Random().Next(10000, 99999).ToString();

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The forgejo labeled-issue fixture with a fresh delivery/issue
    /// identity and the given label present in the verbatim provider payload
    /// (top-level <c>label</c> + <c>issue.labels[]</c>, the webhook shapes).</summary>
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
        // The adapter-normalized field: the kernel reads this, never the payload.
        data["labels"] = new JsonArray(label);
        return doc.ToJsonString();
    }
}
