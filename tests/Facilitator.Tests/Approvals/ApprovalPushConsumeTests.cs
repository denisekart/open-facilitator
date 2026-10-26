using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;

using Microsoft.Extensions.DependencyInjection;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Approvals;

/// <summary>
/// Plan 12 kernel wire delta: the Link-plane consume command (row-ownership
/// gate, role gate, single-use 409s) and the decided-push (every terminal
/// transition enqueues ONE <c>link.&lt;endpoint&gt;</c> outbox row with a
/// contract-valid <c>approval.decided</c> envelope, atomically with the flip;
/// approvals recorded off the Link plane push nothing).
/// </summary>
[Collection("postgres:work")]
public sealed class ApprovalPushConsumeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LinkRecordedApprovalCarriesRecorderAndApprovePushesDecidedOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "decided push");
        var approvalId = Guid.NewGuid();

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var row = await fixture.Db.Approval(approvalId);
        Assert.NotNull(row);
        Assert.Equal("pending", row.State);
        Assert.Equal(endpoint, row.RequestedByEndpointId);

        var alice = await CreateUserClientAsync(factory, "alice");
        var responded = await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        // Same-tx decided-push: one link-addressed row, envelope passes the schema.
        var pushes = await fixture.Db.DecidedPushRows(approvalId);
        var push = Assert.Single(pushes);
        Assert.Equal($"link.{endpoint}", push.Recipient);
        var envelope = JsonDocument.Parse(push.Envelope).RootElement;
        Assert.Equal("org.facilitator.event.approval.decided.v1", envelope.GetProperty("type").GetString());
        Assert.Equal($"approval/{approvalId}", envelope.GetProperty("subject").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal("approved", data.GetProperty("decision").GetString());
        Assert.StartsWith("kernel:", data.GetProperty("evidenceRef").GetString());
        Assert.Equal(workId.ToString(), data.GetProperty("workId").GetString());
        var validator = factory.Services.GetRequiredService<EnvelopeValidator>();
        Assert.True(validator.Validate(push.Envelope).Ok, "decided envelope must be contract-valid");

        // Law D5: the kernel: receipt pushed to the Gateway is the one the
        // REST responder got back.
        Assert.Equal(responded.GetProperty("evidenceRef").GetString(),
            data.GetProperty("evidenceRef").GetString());

        // Replay respond (409) never enqueues a second push row.
        using var replay = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Single(await fixture.Db.DecidedPushRows(approvalId));
    }

    [Fact]
    public async Task ConsumeFlipsApprovedRowOnceAndAudits()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume happy");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        var body = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ConsumeEnvelope(approvalId), HttpStatusCode.Accepted);
        Assert.Equal("consumed", body.GetProperty("status").GetString());
        Assert.Equal("consumed", (await fixture.Db.Approval(approvalId))!.State);
        Assert.Contains(await fixture.Db.AuditRows(actor: $"endpoint:{endpoint}"),
            a => a.Action == "authorization.evaluated:approval" && a.Decision == "allow" && a.Reason == "consumed");

        // Single use: the second consume is a 409 already-consumed.
        using var second = await link.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("approval-already-consumed", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConsumeBeforeRespondDeniesNotPending()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume early");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("approval-not-pending", await response.Content.ReadAsStringAsync());
        Assert.Equal("pending", (await fixture.Db.Approval(approvalId))!.State);
    }

    [Fact]
    public async Task ConsumeAfterRevokeDeniesRevokedAndPushesRevokedOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume revoked");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);

        // Revoke over the Link plane (approval.revoked event arm).
        var revoked = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.revoked.json"))!;
        revoked["id"] = "evt-" + Guid.NewGuid().ToString("N");
        revoked["data"]!["approvalId"] = approvalId.ToString();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", revoked.ToJsonString(), HttpStatusCode.Accepted);

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("approval-revoked", await response.Content.ReadAsStringAsync());

        var push = Assert.Single(await fixture.Db.DecidedPushRows(approvalId));
        Assert.Equal($"link.{endpoint}", push.Recipient);
        Assert.Equal("revoked", JsonDocument.Parse(push.Envelope).RootElement
            .GetProperty("data").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ConsumeByForeignEndpointOrChildRoleDenies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var owner = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var foreign = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var child = "ws-p12-" + Guid.NewGuid().ToString("N")[..8];
        var ownerLink = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, owner));
        var foreignLink = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, foreign));
        var childLink = factory.CreateLinkClient(await EnrollChildAsync(kernel, child));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume foreign");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(ownerLink, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        // Another gateway: 404 — no cross-endpoint existence leak.
        using var wrong = await foreignLink.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Contains("approval-not-found", await wrong.Content.ReadAsStringAsync());

        // A child-role endpoint: 403 — consume is gateway-only.
        using var childCall = await childLink.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, childCall.StatusCode);
        Assert.Contains("approval-actor-unknown", await childCall.Content.ReadAsStringAsync());

        // The row stays approved for the owner.
        Assert.Equal("approved", (await fixture.Db.Approval(approvalId))!.State);
    }

    [Fact]
    public async Task LazyExpirePushesExpiredExactlyOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-p12-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "lazy push");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromMinutes(5)), HttpStatusCode.Accepted);
        factory.Clock!.Advance(TimeSpan.FromMinutes(10));

        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/approvals", HttpStatusCode.OK);
        var pushes = await fixture.Db.DecidedPushRows(approvalId);
        var push = Assert.Single(pushes);
        Assert.Equal($"link.{endpoint}", push.Recipient);
        var data = JsonDocument.Parse(push.Envelope).RootElement.GetProperty("data");
        Assert.Equal("expired", data.GetProperty("decision").GetString());
        Assert.Equal("", data.GetProperty("decidedBy").GetString());

        // Second touch: nothing flips, no second row.
        await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/approvals", HttpStatusCode.OK);
        Assert.Single(await fixture.Db.DecidedPushRows(approvalId));
    }

    [Fact]
    public async Task CloudEventPlaneApprovalPushesNothing()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "no push");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);
        Assert.Empty(await fixture.Db.DecidedPushRows(approvalId));
        Assert.Equal("", (await fixture.Db.Approval(approvalId))!.RequestedByEndpointId);
    }

    // ---- helpers (mirror ApprovalLifecycleTests' local builders) -------------

    private static readonly JsonNode DefaultAction = JsonNode.Parse(
        """{"operation":"publish","resource":"repository:service-api"}""")!;

    private static string RequiredEnvelope(
        Guid approvalId, Guid? workId, IReadOnlyList<string> actors, TimeSpan life)
    {
        var now = DateTime.UtcNow.RoundAsWire();
        var expires = (DateTime.UtcNow + life).RoundAsWire();
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.required.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["approvalId"] = approvalId.ToString();
        data["eligibleActors"] = new JsonArray(actors.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        data["expiresAt"] = expires;
        data["createdAt"] = now;
        data["nonce"] = "n-" + Guid.NewGuid().ToString("N");
        data["action"] = JsonNode.Parse(DefaultAction.ToJsonString());
        if (workId is not null)
        {
            data["workId"] = workId.Value.ToString();
        }

        data["actionDigest"] = Facilitator.Kernel.Shared.ApprovalDigest.Compute(
            approvalId.ToString(),
            JsonDocument.Parse(DefaultAction.ToJsonString()).RootElement,
            actors,
            DateTime.Parse(expires, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        return envelope.ToJsonString();
    }

    private static string ConsumeEnvelope(Guid approvalId)
    {
        return JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = "evt-" + Guid.NewGuid().ToString("N"),
            source = "urn:workspace:gw.test",
            type = "org.facilitator.command.approval.consume.v1",
            subject = $"approval/{approvalId}",
            time = DateTime.UtcNow.RoundAsWire(),
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/command.approval.consume.v1.json",
            data = new
            {
                commandId = "cmd-" + Guid.NewGuid().ToString("N"),
                approvalId = approvalId.ToString(),
                actorEvidenceRef = "workload:gw.test",
            },
        });
    }

    private static async Task<string> EnrollChildAsync(HttpClient client, string endpointId)
    {
        var body = JsonSerializer.Serialize(new
        {
            token = "enroll-dev", endpointId, instanceId = "inst-1",
            trustMode = "isolated-executor", team = "team-alpha", role = "child",
        });
        var json = await KernelOps.PostJsonAsync(client, "/api/link/v1/enroll", body, HttpStatusCode.Created);
        return json.GetProperty("linkToken").GetString()!;
    }

    private static async Task<HttpClient> CreateUserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

file static class PushWireTime
{
    /// <summary>RFC 3339 second precision — the digest law's canonical rendering.</summary>
    internal static string RoundAsWire(this DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
