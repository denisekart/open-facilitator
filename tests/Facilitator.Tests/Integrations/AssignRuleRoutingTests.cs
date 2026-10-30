using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Infrastructure;

namespace Facilitator.Tests.Integrations;

/// <summary>
/// Plan 24(1a): label-routed interim assign. A Work-creating issue event whose
/// verbatim <c>payload</c> carries a rule's label routes the assign to the
/// rule's capability + endpoint (coding legs); a non-matching label (or no
/// rule) keeps the global <c>AssignEndpointId</c>/<c>AssignCapabilityId</c>
/// behavior byte-for-byte (the 22/23 regression), and a rule with a blank
/// endpoint falls back to the global endpoint while keeping its capability.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class AssignRuleRoutingTests(PostgresFixture fixture) : IDisposable
{
    private const string Repo = "demo/widgets";

    private readonly List<KernelTestFactory> _factories = [];

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
        var (link, endpoint) = await PlaneWithRulesAsync(globalEndpoint, codingEndpoint);
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:coding")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        // Rule routing: coding.implement addressed to link.<rule endpoint>,
        // rule-keyed command id, repo/issue re-hydration inputs.
        var assign = await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId}");
        Assert.NotNull(assign);
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
    }

    [Fact]
    public async Task NonMatchingLabelKeepsGlobalInterimAssign()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _) = await PlaneWithRulesAsync(globalEndpoint, codingEndpoint);
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:other")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        // 22/23 regression: the global pair rides the original command id.
        var assign = await fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId}");
        Assert.NotNull(assign);
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal(
            "analysis.summarize",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{globalEndpoint}", recipients);
        Assert.DoesNotContain($"link.{codingEndpoint}", recipients);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId}"));
    }

    [Fact]
    public async Task RuleWithBlankEndpointFallsBackToGlobalEndpoint()
    {
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _) = await PlaneWithRulesAsync(globalEndpoint, codingEndpointId: "");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(IssueEvent(
            delivery, Repo, issue, "alice", label: "ofac:coding")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);
        var assign = await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId}");
        Assert.NotNull(assign);
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal(
            "coding.implement",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{globalEndpoint}", recipients);
    }

    // ---- plane ------------------------------------------------------------

    private async Task<(HttpClient Link, string AdapterEndpoint)> PlaneWithRulesAsync(
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
        return (link, endpoint);
    }

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
        return doc.ToJsonString();
    }
}
