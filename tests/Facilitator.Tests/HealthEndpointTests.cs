using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Facilitator.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsOkWithStatusOk()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }
}
