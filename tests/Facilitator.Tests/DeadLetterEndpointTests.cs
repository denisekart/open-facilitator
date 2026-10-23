using System.Net;

using Facilitator.Kernel.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// appendix §5.6 (A:157): dead-lettered rows become visible on
/// GET /api/dead-letters (kernel-token auth) with reason + the original
/// envelope, newest first, capped at 50.
/// </summary>
[Collection("postgres")]
public sealed class DeadLetterEndpointTests(PostgresFixture fixture)
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
            await fixture.ExecuteAsync(
                $"UPDATE facilitator.outbox SET available_at = now() - interval '1 hour' WHERE work_id = '{workId}' AND status = 'pending'");
        }

        var list = await KernelOps.GetJsonAsync(client, "/api/dead-letters", HttpStatusCode.OK);
        Assert.True(list.GetArrayLength() >= 5, "every dead-lettered row is visible");

        var mine = list.EnumerateArray()
            .Where(d => d.GetProperty("envelope").TryGetProperty("subject", out var s) &&
                        s.GetString()!.Contains(workId.ToString()))
            .ToArray();
        Assert.Equal(5, mine.Length);
        foreach (var entry in mine)
        {
            Assert.Equal("sink refused the delivery", entry.GetProperty("reason").GetString());
            Assert.True(entry.GetProperty("envelope").TryGetProperty("specversion", out _), "original envelope preserved");
            Assert.True(Guid.TryParse(entry.GetProperty("deadLetterId").GetString(), out _), "deadLetterId is a guid");
        }

        // newest-first ordering
        var stamps = list.EnumerateArray().Select(d => d.GetProperty("createdAt").GetDateTime()).ToArray();
        Assert.Equal(stamps.OrderByDescending(t => t).ToArray(), stamps);
    }

    [Fact]
    public async Task EndpointRequiresTheKernelToken()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync("/api/dead-letters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
