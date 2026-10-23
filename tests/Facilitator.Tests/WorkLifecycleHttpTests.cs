using System.Net;


namespace Facilitator.Tests;

/// <summary>
/// §26.1 item 2: a Work driven purely over HTTP walks
/// open→planning→active→review→completed with rising expectedVersion; the
/// timeline records exactly created + 4 state.transition rows; the dispatcher
/// drains every kernel.log outbox row and each envelope is contract-valid.
/// </summary>
[Collection("postgres")]
public class WorkLifecycleHttpTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FullLifecycle_WalksStates_TimelineAndOutboxComplete()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();

        var (workId, version) = await KernelOps.CreateWorkAsync(client, "compare retention alternatives");
        Assert.Equal(1, version);
        Assert.Equal("open", (await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK)).GetProperty("state").GetString());

        await KernelOps.WalkToCompletedAsync(client, workId, 1);

        var final = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("completed", final.GetProperty("state").GetString());
        Assert.Equal(5, final.GetProperty("version").GetInt64());

        var timeline = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}/timeline", HttpStatusCode.OK);
        var rows = timeline.EnumerateArray().ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal("created", rows[0].GetProperty("kind").GetString());
        Assert.Equal(Enumerable.Repeat("state.transition", 4), rows.Skip(1).Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal([1, 2, 3, 4, 5], rows.Select(r => r.GetProperty("workVersion").GetInt64()).ToArray());

        // 1 create + 4 transitions = 5 kernel.log rows; the background timer
        // drains within its 250 ms–5 s window, so poll instead of sleeping.
        await KernelOps.PollUntilAsync(async () =>
            await fixture.CountAsync($"SELECT count(*) FROM facilitator.outbox WHERE work_id = '{workId}' AND status = 'delivered'") == 5,
            "five delivered kernel.log outbox rows");

        var envelopes = await fixture.TextListAsync(
            $"SELECT envelope::text FROM facilitator.outbox WHERE work_id = '{workId}'");
        Assert.Equal(5, envelopes.Count);
        KernelOps.AssertEnvelopesValid(envelopes);
        var recipients = await fixture.TextListAsync(
            $"SELECT DISTINCT recipient_endpoint_id FROM facilitator.outbox WHERE work_id = '{workId}'");
        Assert.Equal(["kernel.log"], recipients);
    }
}
