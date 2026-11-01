using System.Net;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// appendix §5.6 (A:157): dead-lettered rows become visible on
/// GET /api/admin/deliveries?state=dead_letter (identity-gated, admin.read)
/// with reason, newest first (Plan 20 replaced GET /api/dead-letters; the
/// envelope round-trip is proven by the replay test — AdminSurfaceTests).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class DeadLetterEndpointTests(DispatchDbFixture fixture)
{
    private sealed class PoisonSink : ISink
    {
        public string Channel => "kernel";

        public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task DeadLetteredRowsAppearOnTheVisibilityEndpoint()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, _ => new PoisonSink());
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "dead letter visibility");
        await KernelOps.WalkToCompletedAsync(client, workId, 1);

        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        var list = await KernelOps.GetJsonAsync(client, "/api/admin/deliveries?state=dead_letter&limit=200", HttpStatusCode.OK);
        var items = list.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 5, "every dead-lettered row is visible");

        var mine = items.EnumerateArray()
            .Where(d => d.GetProperty("endpointId").GetString() == "kernel.log")
            .ToArray();
        Assert.True(mine.Length >= 5);
        foreach (var entry in mine)
        {
            Assert.Equal("dead_letter", entry.GetProperty("state").GetString());
            Assert.Equal("sink refused the delivery", entry.GetProperty("reason").GetString());
            Assert.True(Guid.TryParse(entry.GetProperty("id").GetString(), out _), "dead-letter id is a guid");
        }

        // newest-first ordering
        var stamps = items.EnumerateArray().Select(d => d.GetProperty("updatedAt").GetDateTime()).ToArray();
        Assert.Equal(stamps.OrderByDescending(t => t).ToArray(), stamps);
    }

    [Fact]
    public async Task EndpointRequiresTheKernelToken()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync("/api/admin/deliveries");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
