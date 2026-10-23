using System.Net;
using System.Text;

using Facilitator.Kernel.Link;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// The five OpenAPI paths carry the same semantics as the hub (fixture corpus
/// passes over both transports): pre-handshake 404 session-not-found,
/// capabilities work without a session, the maxInFlight window bounds claims,
/// receipts free capacity for the rest, and a duplicate command answers
/// duplicate via the SAME inbox-dedupe seam as Plan 02 ingress.
/// </summary>
[Collection("postgres")]
public sealed class HttpFallbackParityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeliveriesRequireALiveSessionButCapabilitiesDoNot()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-hp-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);

        // capabilities works WITHOUT any handshake (locked: 204 on POST).
        using (var post = await link.PostAsync("/api/link/v1/capabilities",
                   new StringContent(LinkTestHarness.Manifest("default"), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        }

        var stored = await KernelOps.GetJsonAsync(link, "/api/link/v1/capabilities", HttpStatusCode.OK);
        Assert.Equal("default", stored.GetProperty("ownerTeam").GetString());

        // ... but the delivery read demands a live link_sessions row (locked rule).
        using var probe = await link.GetAsync("/api/link/v1/deliveries?limit=1");
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
        var body = await probe.Content.ReadAsStringAsync();
        Assert.Contains("session-not-found", body);
    }

    [Fact]
    public async Task ClaimsAreBoundedByMaxInFlightAndReceiptsFreeTheWindow()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, settings: new Dictionary<string, string?> { ["Link:MaxInFlight"] = "2" });
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-hp-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var sessions = factory.Services.GetRequiredService<LinkService>();
        await sessions.OpenSessionAsync(endpoint, "http-1", null, 300, CancellationToken.None);

        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, LinkTestHarness.AssignEnvelopeFor(endpoint));
        }

        var claimed = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
        Assert.Equal(2, claimed.GetArrayLength()); // maxInFlight=2 caps the window
        foreach (var item in claimed.EnumerateArray())
        {
            ids.Add(item.GetProperty("deliveryId").GetString()!);
        }

        Assert.Equal(2, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.link_deliveries WHERE endpoint_id = '{endpoint}' AND state = 'pending'"));

        foreach (var id in ids)
        {
            using var receipt = await link.PostAsync(
                $"/api/link/v1/deliveries/{id}/receipt",
                new StringContent(LinkTestHarness.ReceiptBody("accepted"), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, receipt.StatusCode);
        }

        var rest = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
        Assert.Equal(2, rest.GetArrayLength()); // window freed ⇒ the remainder is claimable
    }

    [Fact]
    public async Task DuplicateCommandAnswersDuplicateOverHttp()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-hp-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);

        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "link steer parity");
        var steer = PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", workId, version);

        var first = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", steer, HttpStatusCode.Accepted);
        Assert.Equal("accepted", first.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("deliveryId").GetString()));

        // same envelope (source+id): the inbox dedupe seam answers duplicate.
        using var second = await link.PostAsync("/api/link/v1/events", new StringContent(steer, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var dup = await second.Content.ReadJsonAsync();
        Assert.Equal("duplicate", dup.GetProperty("status").GetString());

        // the steer really applied: the work version advanced.
        var work = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal(version + 1, work.GetProperty("version").GetInt64());
    }
}
