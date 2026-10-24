using System.Net;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// The AddLinkServer migration ships its tables, the LinkSink registers on the
/// dispatcher's channel table, and the BootstrapTokenMiddleware path exclusion
/// leaks nowhere: /api/works keeps behaving exactly as Plan 02 proved, while
/// the link surface answers 401 for missing/unknown link tokens.
/// </summary>
[Collection("postgres")]
public sealed class MigrationAndAuthBoundaryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LinkTablesExistAndSinkIsRegistered()
    {
        var tables = await fixture.TextListAsync(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'facilitator'");
        foreach (var table in new[] { "link_enrollments", "link_deliveries", "link_sessions" })
        {
            Assert.Contains(table, tables);
        }

        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var sinks = factory.Services.GetRequiredService<IEnumerable<ISink>>();
        Assert.Contains("link", sinks.Select(s => s.Channel));
        Assert.Contains("kernel", sinks.Select(s => s.Channel));
    }

    [Fact]
    public async Task KernelApiStillRequiresTheKernelToken()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);

        using (var anonymous = await factory.CreateClient().GetAsync("/api/works"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var authed = await factory.CreateAuthedClient().GetAsync("/api/works"))
        {
            Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
        }
    }

    [Fact]
    public async Task LinkSurfaceRejectsMissingAndUnknownTokens()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);

        foreach (var token in new[] { null, "not-a-real-link-token" })
        {
            var client = factory.CreateClient();
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.GetAsync("/api/link/v1/deliveries?limit=1");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await response.Content.ReadJsonAsync();
            Assert.Equal("https://errors.openfacilitator.org/v1/unauthorized", problem.GetProperty("type").GetString());
        }
    }
}
