using System.Net;
using System.Text;

namespace Facilitator.Tests.Ingress;

/// <summary>
/// Wire guards at the ingress edge — 256 KiB body limit,
/// bootstrap-token auth on /api (anonymous /health), 404 problem for unknown
/// works.
/// </summary>
[Collection("postgres:ingress")]
public class IngressValidationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OversizedBody_ReturnsPayloadTooLarge()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();

        // Valid CloudEvent envelope padded past 256 KiB via a huge directive.
        var envelope = PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", Guid.NewGuid(), 1);
        var padding = new string('x', 300 * 1024);
        var oversized = envelope.Replace("Focus on cost comparisons only.", padding);
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > 262144);

        using var response = await client.PostAsync("/api/ingress/events",
            new StringContent(oversized, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/payload-too-large", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task MissingBearerToken_ReturnsUnauthorizedProblem()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/works",
            new StringContent("""{"objective":"anon"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/unauthorized", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateClient();

        var health = await KernelOps.GetJsonAsync(client, "/health", HttpStatusCode.OK);
        Assert.Equal("ok", health.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownWork_ReturnsWorkNotFound()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();

        using var response = await client.GetAsync($"/api/works/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/work-not-found", problem.GetProperty("type").GetString());
    }
}
