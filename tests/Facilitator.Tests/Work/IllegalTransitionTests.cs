using System.Net;
using System.Text.Json;

namespace Facilitator.Tests.Work;

/// <summary>
/// §26.1 item 3 (edge guard at the HTTP seam): illegal transitions answer 422
/// with the typed problem and the edge name, and leave the persisted row
/// untouched (state + version unchanged).
/// </summary>
[Collection("postgres:work")]
public class IllegalTransitionTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("completed")]
    [InlineData("active")]
    public async Task IllegalEdgeFromOpen_Returns422AndLeavesWorkUntouched(string to)
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, $"illegal edge to {to}");

        using var response = await client.PostAsync(
            $"/api/works/{workId}/transitions",
            new StringContent(JsonSerializer.Serialize(new { to, expectedVersion = 1 }), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/invalid-state-transition", problem.GetProperty("type").GetString());
        Assert.Contains($"open→{to}", problem.GetProperty("detail").GetString());

        var after = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("open", after.GetProperty("state").GetString());
        Assert.Equal(1, after.GetProperty("version").GetInt64());
        Assert.Equal(1, await fixture.Db.TimelineCount(workId));
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId));
    }
}
