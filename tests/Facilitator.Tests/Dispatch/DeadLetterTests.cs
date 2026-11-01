using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// appendix §5.2/§5.6: a sink that always refuses (returns false) burns the
/// five charged attempts and dead-letters the row — a <c>dead_letters</c> row
/// with reason + original envelope, outbox <c>status='dead_letter'</c>. Backoff
/// (2^a·2s, capped) is side-stepped by backdating <c>available_at</c> between
/// drains so each attempt is immediate and deterministic.
/// </summary>
[Collection("postgres:dispatch-link")]
public class DeadLetterTests(DispatchDbFixture fixture)
{
    private sealed class PoisonSink : ISink
    {
        public string Channel => "kernel";

        public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task RefusingSink_DeadLettersAfterFiveAttempts()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, _ => new PoisonSink());
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "dead letter path");
        await KernelOps.WalkToCompletedAsync(client, workId, 1);

        // Cross-test isolation: a prior suite's link_sessions row stays live
        // until its DB lease expires (90 s default), and the global claim
        // would hand those link.* rows to the LinkSink (delivered > 0).
        // Expire every lease first: link rows then answer no-route (Plan 02
        // requeue, never a delivery) and only the poisoned kernel.log rows
        // burn attempts here.
        await fixture.Db.ExpireLinkLeases();

        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(await dispatcher.DrainOnceAsync(CancellationToken.None) == 0, "refusing sink never delivers");
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        Assert.Equal(5, await fixture.Db.OutboxCountBy(workId, "dead_letter", attempts: 5));
        Assert.Equal(5, await fixture.Db.DeadLetterCount(workId, "sink refused the delivery"));
        Assert.Equal(0, await fixture.Db.OutboxCountBy(workId, "pending"));
    }
}
