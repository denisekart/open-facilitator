using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// Public-only replication of the feature-suite helpers a walk needs (the
/// internals of <c>Facilitator.Tests` are invisible here): link enrollment
/// over the raw route, user-client minting, and the envelope builders copied
/// verbatim from the segment sources named in the manifest. Walks read
/// response bodies through plain System.Text.Json (JsonWire is internal).
/// </summary>
internal static class WalkKit
{
    internal static readonly JsonObject HeldAction = JsonNode.Parse(
        """{"operation":"publish","resource":"repository:service-api"}""")!.AsObject();

    internal static readonly JsonObject HeldCommand = JsonNode.Parse(
        """
        {
          "specversion": "1.0",
          "id": "cmd-held-1",
          "source": "urn:workspace:gw.test",
          "type": "org.facilitator.command.work.assign.v1",
          "subject": "work/00000000-0000-0000-0000-000000000000",
          "time": "2026-01-01T00:00:00Z",
          "datacontenttype": "application/json",
          "dataschema": "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
          "data": {
            "commandId": "cmd-held-1",
            "workId": "00000000-0000-0000-0000-000000000000",
            "targetId": "child-1",
            "capabilityId": "coding.implement",
            "expectedContextVersion": 1,
            "inputs": {"marker": "original"},
            "expectedDeliverables": [],
            "actorEvidenceRef": "workload:gw.test",
            "deadline": "2026-01-02T00:00:00Z",
            "assignmentId": "asg-held-1",
            "executionGeneration": 1
          }
        }
        """)!.AsObject();

    /// <summary>Enroll a link endpoint (Development bootstrap secret) over the
    /// raw route — the public-only twin of the internal LinkTestHarness.</summary>
    internal static async Task<string> EnrollAsync(HttpClient kernel, string endpointId, string team = "team-alpha")
    {
        var body = JsonSerializer.Serialize(new { token = "enroll-dev", endpointId, instanceId = "inst-1", trustMode = "isolated-executor", team });
        var json = await KernelOps.PostJsonAsync(kernel, "/api/link/v1/enroll", body, HttpStatusCode.Created);
        return json.GetProperty("linkToken").GetString()!;
    }

    /// <summary>A client carrying a link token (not the kernel bearer).</summary>
    internal static HttpClient LinkClient(this KernelTestFactory factory, string linkToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", linkToken);
        return client;
    }

    /// <summary>A client acting as a realm user (password grant token).</summary>
    internal static async Task<HttpClient> UserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>The canonical identity a token's subject maps to.</summary>
    internal static async Task<string> CanonicalActorAsync(string user)
    {
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            token.Split('.')[1].PadRight(token.Split('.')[1].Length + (4 - token.Split('.')[1].Length % 4) % 4, '='))));
        return $"oidc:{KeycloakFixture.Issuer}|{payload.RootElement.GetProperty("sub").GetString()}";
    }

    internal static async Task<JsonElement> ReadJsonAsync(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>An approval-required event (the ConsentAuthorityTests twin):
    /// fresh envelope id + digest over the exact coordinates written.</summary>
    internal static string RequiredEnvelope(
        Guid approvalId, Guid? workId, IReadOnlyList<string> actors, TimeSpan life,
        JsonObject? command = null, string? requestedBy = null)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var expires = (DateTime.UtcNow + life).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.required.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["approvalId"] = approvalId.ToString();
        data["eligibleActors"] = new JsonArray(actors.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        data["expiresAt"] = expires;
        data["createdAt"] = now;
        data["nonce"] = "n-" + Guid.NewGuid().ToString("N");
        data["action"] = HeldAction.DeepClone().AsObject();
        if (workId is not null)
        {
            data["workId"] = workId.Value.ToString();
        }

        if (command is not null)
        {
            data["command"] = command.DeepClone().AsObject();
        }

        if (requestedBy is not null)
        {
            data["requestedBy"] = requestedBy;
        }

        data["actionDigest"] = ApprovalDigest.Compute(
            approvalId.ToString(),
            JsonDocument.Parse(HeldAction.ToJsonString()).RootElement,
            actors,
            DateTime.Parse(expires, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        return envelope.ToJsonString();
    }

    internal static string ConsumeEnvelope(Guid approvalId) =>
        JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = "evt-" + Guid.NewGuid().ToString("N"),
            source = "urn:workspace:gw.test",
            type = "org.facilitator.command.approval.consume.v1",
            subject = $"approval/{approvalId}",
            time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/command.approval.consume.v1.json",
            data = new
            {
                commandId = "cmd-" + Guid.NewGuid().ToString("N"),
                approvalId = approvalId.ToString(),
                actorEvidenceRef = "workload:gw.test",
            },
        });

    /// <summary>A provider event for ONE native message at a chosen delivery
    /// id (the ProviderIngressTests builder twin).</summary>
    internal static string ProviderEvent(
        string fixtureName, string provider, string deliveryId, string channel, string root, string actor, string? text = null) =>
        ProviderEvent(fixtureName, provider, null, deliveryId, channel, root, actor, text);

    internal static string ProviderEvent(
        string fixtureName, string provider, string? tenant, string deliveryId, string channel, string root, string actor, string? text = null)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture(fixtureName))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["provider"] = provider;
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["provider"] = provider;
        if (tenant is not null)
        {
            reference["tenantOrOrganizationId"] = tenant;
        }

        reference["channelOrResourceId"] = channel;
        reference["rootThreadId"] = root;
        reference["messageId"] = deliveryId;
        reference["authorProviderAccountId"] = actor;
        if (text is not null)
        {
            data["text"] = text;
        }

        return doc.ToJsonString();
    }

    internal static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    internal static string Leaf() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Seed a live fleet (enrollment + manifest + leased session)
    /// advertising capabilities on one team — the public-only twin of the
    /// internal FleetHarness.</summary>
    internal static async Task SeedFleetAsync(TestDbProbe db, string endpoint, string team, params string[] capabilities)
    {
        await db.SeedGatewayEnrollment(endpoint, team, "isolated-executor");
        await db.SeedCapabilityManifest(endpoint, JsonSerializer.Serialize(new
        {
            apiVersion = "facilitator.framework/v1",
            kind = "CapabilityManifest",
            ownerTeam = team,
            capabilities = capabilities.Select(c => new { id = c, version = 1, execution = "asynchronous" }).ToArray(),
        }));
        await db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow - TimeSpan.FromHours(2));
    }

    /// <summary>Drain the dispatcher repeatedly until the probe lands (the
    /// public-only twin of the internal RoutingHarness): foreign hosts'
    /// dispatchers may claim a row from a shared DB, so one-shot drains are a flake.</summary>
    internal static async Task<T> DrainUntilAsync<T>(KernelTestFactory factory, Func<Task<T?>> probe, string what)
        where T : class
    {
        var dispatcher = factory.Services
            .GetRequiredService<Facilitator.Kernel.Shared.Dispatch.IEventDispatcher>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"routing effect for {what} never landed");
    }

    /// <summary>The mattermost plane the provider-ingest segments configure.</summary>
    internal static Dictionary<string, string?> MattermostPlane(string endpoint, string? botUserId = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
            ["Providers:Mattermost:LinkHintText"] = "Your Mattermost account is not linked to a Facilitator identity — send me a DM starting with /link to link it.",
        };
        if (botUserId is not null)
        {
            settings["Providers:Mattermost:BotUserId"] = botUserId;
        }

        return settings;
    }
}
