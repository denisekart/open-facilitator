using System.Net.Http.Headers;
using System.Text.Json;

using Facilitator.Kernel.Dispatch;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// WebApplicationFactory over the real kernel composition: Development env
/// (bearer <c>dev-token</c>), migrations on, connection string pointed at the
/// shared container. Variants disable the OutboxDispatcher background timer
/// (outbox rows stay <c>pending</c> until a test drives DrainOnceAsync) or
/// replace the <c>kernel.log</c> stub sink (dead-lettering, refusing sinks).
/// </summary>
public sealed class KernelTestFactory : WebApplicationFactory<Program>
{
    private readonly PostgresFixture _fixture;
    private readonly bool _noDispatcher;
    private readonly Func<IServiceProvider, ISink>? _sinkOverride;

    /// <summary>Create a factory bound to the shared container.</summary>
    /// <param name="fixture">Shared Postgres container.</param>
    /// <param name="noDispatcher">True removes the OutboxDispatcher hosted service.</param>
    /// <param name="sinkOverride">When set, replaces the kernel.log stub sink.</param>
    public KernelTestFactory(PostgresFixture fixture, bool noDispatcher = false, Func<IServiceProvider, ISink>? sinkOverride = null)
    {
        _fixture = fixture;
        _noDispatcher = noDispatcher;
        _sinkOverride = sinkOverride;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("ConnectionStrings:Facilitator", _fixture.ConnectionString);
        builder.ConfigureTestServices(services =>
        {
            if (_noDispatcher)
            {
                foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(OutboxDispatcher)).ToArray())
                {
                    services.Remove(descriptor);
                }
            }

            if (_sinkOverride is not null)
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ISink) && d.ImplementationType == typeof(KernelLogSink)).ToArray())
                {
                    services.Remove(descriptor);
                }

                // Last registration per channel wins in DbEventDispatcher.
                services.AddSingleton(_sinkOverride);
            }
        });
    }

    /// <summary>An authenticated client (bearer <c>dev-token</c>, JSON accepts).</summary>
    public HttpClient CreateAuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dev-token");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

/// <summary>Tiny JSON helpers shared across the Plan 02 suites.</summary>
internal static class Json
{
    /// <summary>Parse a response body as JSON (detached clone).</summary>
    internal static async Task<JsonElement> ReadJsonAsync(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync()).RootElement.Clone();
}
