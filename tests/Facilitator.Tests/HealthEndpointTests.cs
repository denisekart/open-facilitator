using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Facilitator.Tests;

/// <summary>
/// The health endpoint is anonymous and must stay reachable without a
/// database: migrations are disabled via Database:AutoMigrate and the
/// dispatcher tolerates the missing connection (catch + log per loop).
/// </summary>
public class HealthEndpointNoDatabaseFactory : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:AutoMigrate", "false");
        builder.UseSetting("Auth:BootstrapToken", "health-test-token");
    }
}

public class HealthEndpointTests(HealthEndpointNoDatabaseFactory factory)
    : IClassFixture<HealthEndpointNoDatabaseFactory>
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
