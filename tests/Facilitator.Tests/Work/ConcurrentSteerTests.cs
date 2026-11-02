using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Facilitator.Tests.Work;

/// <summary>
/// Optimistic fencing under real concurrency: two steers racing on the
/// same expectedVersion serialize behind the per-Work advisory lock, so
/// exactly one wins (200) and the loser sees 409 version-conflict carrying
/// the new currentVersion.
/// </summary>
[Collection("postgres:work")]
public class ConcurrentSteerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwoSteersSameFence_ExactlyOneWins()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "concurrent steer fence");

        static string Steer(string commandId) =>
            JsonSerializer.Serialize(new { directive = "focus", commandId, expectedVersion = 1 });

        var firstTask = client.PostAsync($"/api/works/{workId}/steer",
            new StringContent(Steer("cmd-a-" + Guid.NewGuid().ToString("N")), System.Text.Encoding.UTF8, "application/json"));
        var secondTask = client.PostAsync($"/api/works/{workId}/steer",
            new StringContent(Steer("cmd-b-" + Guid.NewGuid().ToString("N")), System.Text.Encoding.UTF8, "application/json"));
        await Task.WhenAll(firstTask, secondTask);
        using var first = await firstTask;
        using var second = await secondTask;

        var statuses = new[] { first.StatusCode, second.StatusCode }.OrderBy(s => (int)s).ToArray();
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.Conflict, statuses[1]);

        var loser = first.StatusCode == HttpStatusCode.Conflict ? first : second;
        var problem = await JsonWire.ReadJsonAsync(loser.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/version-conflict", problem.GetProperty("type").GetString());
        Assert.Equal(2, problem.GetProperty("currentVersion").GetInt64());

        var after = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal(2, after.GetProperty("version").GetInt64());
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "steer"));
    }
}
