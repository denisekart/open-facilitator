using System.Net;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// §26 slice-1 exit test: "Restart after accepted event; no loss or duplicate
/// business command". Factory A runs WITHOUT the OutboxDispatcher timer, so
/// accepted commands' outbox rows are durable but undelivered; A dies. Factory
/// B over the same container drains them exactly once (ledger + DB
/// attempts==1 + every envelope contract-valid), a re-drain is a no-op, and
/// replaying the same business command yields `duplicate` with exactly one
/// steer timeline row.
/// </summary>
[Collection("postgres:dispatch-link")]
public class RestartSurvivalTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task RestartAfterAcceptedEvents_NoLossNoDuplicate()
    {
        Guid workId;
        string[] outboxIds;
        string envelope;

        // --- Life #1: accepted writes, dispatcher disabled ⇒ rows stay pending.
        // 1 create + 4 transitions + 1 cloud-event steer = 6 outbox rows.
        using (var factoryA = new KernelTestFactory(fixture, noDispatcher: true))
        {
            using var clientA = factoryA.CreateAuthedClient();
            (workId, _) = await KernelOps.CreateWorkAsync(clientA, "restart survival");

            await KernelOps.TransitionAsync(clientA, workId, "planning", 1);

            envelope = PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", workId, expectedContextVersion: 2);
            var accepted = await KernelOps.PostJsonAsync(clientA, "/api/ingress/events", envelope, HttpStatusCode.OK);
            Assert.Equal("accepted", accepted.GetProperty("status").GetString());

            await KernelOps.TransitionAsync(clientA, workId, "active", 3);
            await KernelOps.TransitionAsync(clientA, workId, "review", 4);
            await KernelOps.TransitionAsync(clientA, workId, "completed", 5);

            // All six writes are durable before the restart. A dispatcher
            // from another feature's parallel collection may already have
            // drained them (claims are global) — that IS a foreign-host
            // delivery, exactly the restart-surface this test proves.
            await KernelOps.PollUntilAsync(async () =>
                await fixture.Db.OutboxCountBy(workId) == 6,
                "six durable outbox rows before restart");
            outboxIds = (await fixture.Db.OutboxIdTexts(workId)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(6, outboxIds.Length);
        }

        // --- Work row survived the "crash" intact (no loss of business state).
        using var probe = new KernelTestFactory(fixture, noDispatcher: true);
        using var probeClient = probe.CreateAuthedClient();
        var work = await KernelOps.GetJsonAsync(probeClient, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("completed", work.GetProperty("state").GetString());
        Assert.Equal(6, work.GetProperty("version").GetInt64());

        // --- Life #2: fresh app host drains each outbox id exactly once.
        using var factoryB = new KernelTestFactory(fixture, noDispatcher: true);
        var dispatcher = factoryB.Services.GetRequiredService<IEventDispatcher>();
        await KernelOps.PollUntilAsync(async () =>
            (await dispatcher.DrainOnceAsync(CancellationToken.None)) >= 0 &&
            await fixture.Db.OutboxCountBy(workId, "delivered") == 6,
            "restart drains all six rows");

        Assert.Equal(6, await fixture.Db.OutboxCountBy(workId, "delivered", attempts: 1));
        KernelOps.AssertEnvelopesValid(await fixture.Db.OutboxEnvelopes(workId));

        // Durable deliveries view (Plan 20: /api/admin/deliveries replaced
        // the in-memory dev ledger): the table holds exactly one row per
        // outbox id, so a redrain can never duplicate a delivery — the
        // DB-side assert above (six delivered, attempts == 1) stays the
        // exactly-once proof; this one is the one-row-per-id proof.
        using var clientB = factoryB.CreateAuthedClient();
        for (var i = 0; i < 5; i++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
        }

        var deliveries = await KernelOps.GetJsonAsync(clientB, "/api/admin/deliveries?state=delivered&limit=200", HttpStatusCode.OK);
        var deliveredIds = deliveries.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetGuid().ToString("D"))
            .ToArray();
        foreach (var id in outboxIds)
        {
            Assert.Equal(1, deliveredIds.Count(x => x == id));
        }

        // --- Replay the same business command against the restarted kernel.
        var replay = await KernelOps.PostJsonAsync(clientB, "/api/ingress/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "steer"));
    }
}
