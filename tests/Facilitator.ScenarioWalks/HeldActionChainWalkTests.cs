using System.Net;
using System.Text.Json;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// C5→N6→C7 held-action chain: a held command rides an approval to a
/// second human's consumption (C5's consume returns the kernel-stored
/// envelope), the loser sees who decided (C7's paper trail), and the
/// expired leg of the same walk answers the documented denial (N6's
/// lazy-expiry law) — the nudge sweep stamps the reminder exactly once.
/// Segments: ConsentAuthorityTests (expiry, nudge, two-responders),
/// ApprovalLifecycleTests (expired consume denial).
/// </summary>
[Collection("postgres:walks")]
public sealed class HeldActionChainWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task HeldActionNudgedThenDecidedOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig("consent-policy", """{"nudgeBeforeMinutes":10,"expiresAfterMinutes":60}""");
        var endpoint = "gw-walk-" + WalkKit.Leaf();
        var link = factory.LinkClient(await WalkKit.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "held chain");
        var (expiredWorkId, _) = await KernelOps.CreateWorkAsync(kernel, "expired leg");
        var approvalId = Guid.NewGuid();
        var lapsedId = Guid.NewGuid();

        // Segment C5 (persist-first + hold): the approval with a held assign
        // command, two eligible humans, five minutes of life.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.RequiredEnvelope(approvalId, workId, ["user:alice", "user:bob"],
                TimeSpan.FromMinutes(5), command: WalkKit.HeldCommand, requestedBy: "workload:gw.test"),
            HttpStatusCode.Accepted);
        var held = await fixture.Db.HeldAction(approvalId);
        Assert.NotNull(held);
        Assert.Equal("pending", held.State);

        // Segment C7 (nudge leg, driven): inside the nudge lead window, one
        // nudge sweep writes the timeline row and stamps exactly once.
        await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/approvals", "{}", HttpStatusCode.OK);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));
        Assert.True(await fixture.Db.ApprovalNudged(approvalId));
        await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/approvals", "{}", HttpStatusCode.OK);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));

        // Segment C7 (decision): the first human decides through her own
        // identity; the loser's replay sees the ledger name the winner.
        var alice = await WalkKit.UserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);
        // Bob is a plain team member (no operator role): his identity rides
        // minted evidence on the ungated wire plane. His replay: 409, and
        // the ledger names the winner for the reply.
        var bobActor = await KeycloakFixture.ActorOfAsync("bob", "bob");
        await fixture.Db.SeedEvidence("ev-walk-bob-" + approvalId.ToString("N"), bobActor,
            DateTime.UtcNow.AddMinutes(5), null);
        using var loserReplay = await kernel.PostAsync("/api/ingress/events", WalkKit.Content(
            RespondEnvelope(approvalId, "denied", $"kernel:ev-walk-bob-{approvalId:N}")));
        Assert.Equal(HttpStatusCode.Conflict, loserReplay.StatusCode);
        var problem = await loserReplay.Content.ReadJsonAsync();
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-already-consumed",
            problem.GetProperty("type").GetString());
        var listed = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/approvals", HttpStatusCode.OK);
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal("approved", row.GetProperty("state").GetString());
        Assert.StartsWith("oidc:", row.GetProperty("decidedBy").GetString());

        // Segment C5 (consume): the gateway's consume hands back the kernel-
        // stored envelope verbatim — execution runs exactly what consent covered.
        var consumed = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ConsumeEnvelope(approvalId), HttpStatusCode.Accepted);
        Assert.Equal("consumed", consumed.GetProperty("status").GetString());
        Assert.Equal("cmd-held-1", consumed.GetProperty("data").GetProperty("envelope")
            .GetProperty("id").GetString());
        Assert.Equal("delivered", (await fixture.Db.HeldAction(approvalId))!.State);

        // Segment N6 (same walk, expired leg): a second ask lapses past its
        // expiry — the driven advance + sweep parks the work loud, and a
        // slow consume denial rides the same lazy-expiry law.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.RequiredEnvelope(lapsedId, expiredWorkId, ["user:alice"], TimeSpan.FromMinutes(5)),
            HttpStatusCode.Accepted);
        factory.AdvanceClock(TimeSpan.FromMinutes(10));
        await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/approvals", "{}", HttpStatusCode.OK);
        Assert.Equal("expired", (await fixture.Db.Approval(lapsedId))!.State);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(expiredWorkId, "work.consent-expired"));
        var lapsedActor = await KeycloakFixture.ActorOfAsync("bob", "bob");
        // The validator reads the DI TimeProvider — the fake's advanced now
        // — so the evidence must outlive THAT, not the wall clock.
        await fixture.Db.SeedEvidence("ev-walk-" + lapsedId.ToString("N"), lapsedActor,
            factory.ClockUtcNow.UtcDateTime + TimeSpan.FromMinutes(5), null);
        using var late = await kernel.PostAsync("/api/ingress/events", WalkKit.Content(
            RespondEnvelope(lapsedId, "approved", $"kernel:ev-walk-{lapsedId:N}")));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-expired",
            (await late.Content.ReadJsonAsync()).GetProperty("type").GetString());
    }

    private static string RespondEnvelope(Guid approvalId, string decision, string evidenceRef) =>
        JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = "evt-" + Guid.NewGuid().ToString("N"),
            source = "urn:workspace:gw.test",
            type = "org.facilitator.command.approval.respond.v1",
            subject = $"approval/{approvalId}",
            time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/command.approval.respond.v1.json",
            data = new
            {
                commandId = "cmd-" + Guid.NewGuid().ToString("N"),
                approvalId = approvalId.ToString(),
                decision,
                actorEvidenceRef = evidenceRef,
            },
        });

}
