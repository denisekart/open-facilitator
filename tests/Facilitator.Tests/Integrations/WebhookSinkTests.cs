using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Integrations;

/// <summary>
/// The webhook sink + the adapter-normalized event fields. A raw forge
/// delivery posted to <c>POST /api/ingress/webhooks/{provider}</c> lands as a
/// <c>command.provider.webhook.v1</c> outbox row addressed to
/// <c>link.&lt;EndpointId&gt;</c> with verbatim bytes/headers (body-hash dedupe
/// key ⇒ a replayed delivery answers duplicate with one row); a provider
/// without an enabled section answers 404. And the two kernel reads that used
/// to dig vendor payload shapes — the head-branch resolver and the label
/// assign-rule match — prefer the normalized <c>headRef</c>/<c>labels</c>
/// fields when the adapter populates them.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class WebhookSinkTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Repo = "demo/widgets";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
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
    public async Task RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes()
    {
        var (factory, endpoint) = await PlaneAsync();
        var http = factory.CreateClient();
        var raw = """{"action":"opened","number":77001}""";
        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/ingress/webhooks/forgejo")
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json"),
        };
        first.Headers.TryAddWithoutValidation("X-Gitea-Event", "issues");
        first.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=abc");
        var posted = await http.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var body = await posted.Content.ReadJsonAsync();
        var commandId = body.GetProperty("commandId").GetString()!;
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.Equal($"forgejo:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()}",
            commandId[$"webhook:".Length..]);

        // The fan-out row: schema envelope, verbatim bytes + headers, addressed
        // to the provider's adapter endpoint.
        var envelopeJson = await fixture.Db.OutboxEnvelopeByCommand(commandId);
        Assert.NotNull(envelopeJson);
        using var doc = JsonDocument.Parse(envelopeJson);
        var envelope = doc.RootElement;
        Assert.Equal("org.facilitator.command.provider.webhook.v1", envelope.GetProperty("type").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal("forgejo", data.GetProperty("provider").GetString());
        Assert.Equal(commandId, data.GetProperty("commandId").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)), data.GetProperty("body").GetString());
        Assert.StartsWith("application/json", data.GetProperty("contentType").GetString());
        Assert.Equal("issues", data.GetProperty("headers").GetProperty("X-Gitea-Event").GetString());
        Assert.Equal("sha256=abc", data.GetProperty("headers").GetProperty("X-Hub-Signature-256").GetString());
        Assert.Equal($"link.{endpoint}", Assert.Single(await fixture.Db.OutboxRecipientsByCommand(commandId)));

        // Provider retry replays the identical body → duplicate, one row.
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/ingress/webhooks/forgejo")
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json"),
        };
        var second = await http.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("duplicate", (await second.Content.ReadJsonAsync()).GetProperty("status").GetString());
        Assert.Equal(1, await fixture.Db.OutboxCountByCommand(commandId));
    }

    [Fact]
    public async Task UnknownOrDisabledProviderAnswers404()
    {
        var (factory, _) = await ForgejoPlaneAsync();
        var http = factory.CreateClient();
        using var unknown = await http.PostAsync("/api/ingress/webhooks/mattermost",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.EndsWith("provider-unknown",
            (await unknown.Content.ReadJsonAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task OversizedDeliveryAnswers413()
    {
        var (factory, _) = await ForgejoPlaneAsync();
        var http = factory.CreateClient();
        var huge = new string('x', 2 * 1024 * 1024);
        using var response = await http.PostAsync("/api/ingress/webhooks/forgejo",
            new StringContent(huge, Encoding.UTF8, "text/plain"));
        Assert.Equal((HttpStatusCode)413, response.StatusCode);
    }

    [Fact]
    public async Task NormalizedHeadRefResolvesBranchMarkerWithoutPayloadDig()
    {
        // The opened event carries ONLY the normalized headRef (no
        // pull_request.head.ref in the verbatim payload): the resolver reads
        // the normalized field.
        var (factory, link) = await ForgejoPlaneAsync();
        var workId = await fixture.Db.SeedWork("active");
        var head = "ofac/" + Guid.NewGuid().ToString("N")[..12];
        await fixture.Db.SeedBinding("forgejo", Repo, "branch:" + head, workId);

        var pr = new Random().Next(10000, 99999).ToString();
        var posted = await link.PostAsync("/api/link/v1/events",
            new StringContent(OpenedEvent($"dlv-{Guid.NewGuid():N}", Repo, pr, headRef: head, digShape: false),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        Assert.Equal(workId, (await fixture.Db.BindingFor("forgejo", Repo, pr)).WorkId);
    }

    [Fact]
    public async Task NormalizedLabelsDriveAssignRuleWithoutPayloadDig()
    {
        // The labeled event carries ONLY the normalized labels[] (the payload
        // has no label shapes): the rule match reads the normalized field and
        // the coding intent routes to the rule endpoint.
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var (factory, link) = await ForgejoPlaneAsync(globalEndpoint, codingEndpoint);
        await FleetHarness.SeedFleetAsync(fixture.Db, codingEndpoint, "team-alpha", "coding.implement");
        var issue = new Random().Next(10000, 99999).ToString();

        var posted = await link.PostAsync("/api/link/v1/events",
            new StringContent(LabeledEvent($"dlv-{Guid.NewGuid():N}", Repo, issue, "ofac:coding"),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);

        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"),
            "normalized-label coding assign effect");
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal("coding.implement",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
    }

    // ---- plane / fixtures ---------------------------------------------------

    private async Task<(KernelTestFactory Factory, HttpClient Link)> ForgejoPlaneAsync(
        string? globalAssignEndpoint = null, string? codingEndpointId = null)
    {
        var (factory, endpoint) = await PlaneAsync(globalAssignEndpoint, codingEndpointId);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(
            factory.CreateAuthedClient(), endpoint));
        return (factory, link);
    }

    private async Task<(KernelTestFactory Factory, string AdapterEndpoint)> PlaneAsync(
        string? globalAssignEndpoint = null, string? codingEndpointId = null)
    {
        var endpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
        };
        if (globalAssignEndpoint is not null)
        {
            settings["Providers:Forgejo:AssignEndpointId"] = globalAssignEndpoint;
            settings["Providers:Forgejo:AssignRules:0:Label"] = "ofac:coding";
            settings["Providers:Forgejo:AssignRules:0:CapabilityId"] = "coding.implement";
            settings["Providers:Forgejo:AssignRules:0:EndpointId"] = codingEndpointId ?? "";
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        return (factory, endpoint);
    }

    /// <summary>The change_request opened fixture rewritten with fresh
    /// identity; <paramref name="digShape"/> false ships a vendor-blank
    /// payload so ONLY the normalized <c>headRef</c> can resolve the marker.</summary>
    private static string OpenedEvent(string deliveryId, string repo, string number, string headRef, bool digShape)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.change_request.opened.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        if (digShape)
        {
            payload["pull_request"] = new JsonObject { ["head"] = new JsonObject { ["ref"] = headRef } };
        }
        else
        {
            data["headRef"] = headRef;
        }

        return doc.ToJsonString();
    }

    /// <summary>The labeled-issue fixture rewritten with ONLY the normalized
    /// <c>labels[]</c> populated (payload carries no label shapes).</summary>
    private static string LabeledEvent(string deliveryId, string repo, string number, string label)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.labeled.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload.AsObject().Remove("label");
        data["labels"] = new JsonArray(label);
        return doc.ToJsonString();
    }
}
