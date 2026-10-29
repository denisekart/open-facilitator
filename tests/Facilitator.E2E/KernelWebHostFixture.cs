using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.E2E;

/// <summary>
/// The kernel on a real Kestrel socket (random port) for the Playwright tier:
/// Development env, shared Postgres + Keycloak fixtures (linked sources), and
/// <c>ui:oidcAuthority</c> pointed at the container issuer so the SPA's
/// bootstrap document matches the authority the JWTs carry. Dispatcher and
/// sink variants mirror <c>KernelTestFactory</c> (dead-lettering without a
/// live timer racing the test).
/// </summary>
public sealed class KernelWebHostFixture : WebApplicationFactory<Program>
{
    private readonly PostgresFixture _postgres;
    private readonly bool _noDispatcher;
    private readonly Func<IServiceProvider, ISink>? _sinkOverride;

    /// <param name="postgres">Shared Postgres container fixture.</param>
    /// <param name="noDispatcher">True removes the OutboxDispatcher hosted service.</param>
    /// <param name="sinkOverride">When set, replaces the kernel.log stub sink.</param>
    public KernelWebHostFixture(PostgresFixture postgres, bool noDispatcher = false, Func<IServiceProvider, ISink>? sinkOverride = null)
    {
        _postgres = postgres;
        _noDispatcher = noDispatcher;
        _sinkOverride = sinkOverride;
        UseKestrel(0);
    }

    /// <summary>Base address of the Kestrel server (starts the server).</summary>
    public string ServerAddress
    {
        get
        {
            StartServer();
            var addresses = Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses;
            // Prefer the dynamically-bound loopback endpoint over Program's dev-default 8088.
            return addresses.FirstOrDefault(a => a.Contains("127.0.0.1", StringComparison.Ordinal)) ?? addresses.First();
        }
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("ConnectionStrings:Facilitator", _postgres.ConnectionString);
        builder.UseSetting("Identity:Oidc:Authority", KeycloakFixture.Issuer);
        builder.UseSetting("Identity:Oidc:Audience", "facilitator-api");
        builder.UseSetting("Identity:Oidc:RequireHttps", "false");
        // The SPA bootstrap must sign in against the same issuer.
        builder.UseSetting("ui:oidcAuthority", KeycloakFixture.Issuer);
        builder.UseSetting("ui:oidcClientId", "facilitator-api");
        builder.UseSetting("Link:Teams:0", "team-alpha");
        builder.UseSetting("Link:Teams:1", "team-beta");
        builder.UseSetting("Link:Evidence:Audience", "facilitator-link");
        builder.UseSetting("Storage:Gc:IntervalSeconds", "3600");

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

    /// <summary>An authenticated raw client (CLI service-account bearer) for test seeding over HTTP.</summary>
    public HttpClient CreateSeedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", KeycloakFixture.CliToken);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

/// <summary>Refuses every delivery — drives rows to the dead-letter ledger.</summary>
public sealed class PoisonSink : ISink
{
    public string Channel => "kernel";

    public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct) => Task.FromResult(false);
}
