using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// Drain helper for routing-intent assertions on the shared suite database:
/// other collections' factories run real background dispatchers over the same
/// outbox, so an intent may be claimed by a foreign dispatcher at any moment
/// (or miss this test's single-claim batch). Polling repeated drains until the
/// effect/audit lands is the deterministic probe; one-shot drains are a flake.
/// </summary>
internal static class RoutingHarness
{
    internal static async Task<T> DrainUntilAsync<T>(KernelTestFactory factory, Func<Task<T?>> probe, string what)
        where T : class
    {
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"routing effect for {what} never landed");
    }
}

/// <summary>
/// Seed a live fleet (enrollment + manifest + leased session) advertising the
/// given capabilities on one team, and wait for a routing effect row. The
/// session lease keeps the fleet a candidate for the suite run; suites purge
/// by endpoint prefix when a fixed name could collide.
/// </summary>
internal static class FleetHarness
{
    internal static async Task SeedFleetAsync(
        TestDbProbe db, string endpoint, string team, params string[] capabilities)
    {
        await db.SeedGatewayEnrollment(endpoint, team, "isolated-executor");
        await db.SeedCapabilityManifest(endpoint, System.Text.Json.JsonSerializer.Serialize(new
        {
            apiVersion = "facilitator.framework/v1",
            kind = "CapabilityManifest",
            ownerTeam = team,
            capabilities = capabilities.Select(c => new { id = c, version = 1, execution = "asynchronous" }).ToArray(),
        }));
        await db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow - TimeSpan.FromHours(2));
    }
}
