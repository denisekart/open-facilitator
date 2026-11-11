using System.Net;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// N2 automation dialogue: two automation voices on one bound conversation
/// — the bot's own post rides the echo law (ledger move, zero arm noise),
/// the shared loop budget then stops the human's next reply with ONE
/// judgment row, and a redelivery of the stopped delivery stays a
/// duplicate: the budget trips once, not twice.
/// Segments: ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly +
/// LoopBudgetStopsTheConversationAtTheCap.
/// </summary>
[Collection("postgres:walks")]
public sealed class AutomationDialogueWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DialogueBudgetTripsAsOneStopEvent()
    {
        var bot = "bot-" + WalkKit.Leaf();
        var endpoint = "gw-n2-" + WalkKit.Leaf();
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, settings: WalkKit.MattermostPlane(endpoint, bot));
        var kernel = factory.CreateAuthedClient();
        // The two automation actors are two sessions of ONE logical adapter
        // endpoint (the provider gate demands the section's EndpointId):
        // voice one echoes, voice two answers — the kernel sees one loop.
        // One enrollment: a re-enroll rotates the token, so both clients
        // carry the same bearer (the two-socket twin of one adapter).
        var token = await WalkKit.EnrollAsync(kernel, endpoint);
        var linkA = factory.LinkClient(token);
        var linkB = factory.LinkClient(token);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{WalkKit.Leaf()}";
        var root = $"root-{WalkKit.Leaf()}";

        // The ask that binds the conversation (adapter leg one).
        await KernelOps.PostJsonAsync(linkA, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "mattermost",
                $"post-{WalkKit.Leaf()}", channel, root, "acct-1", "dialogue please"),
            HttpStatusCode.Accepted);
        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);

        // Voice one (automation): the bot's post is a self-write — the echo
        // law moves the binding's parent, the Work hears no arm effects.
        var botPost = $"post-{WalkKit.Leaf()}";
        var echo = await KernelOps.PostJsonAsync(linkB, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.message.json", "mattermost",
                botPost, channel, root, bot),
            HttpStatusCode.Accepted);
        Assert.Equal("echo:post-ledger",
            echo.GetProperty("data").GetProperty("judgment").GetString());
        var (bound, parent) = await fixture.Db.BindingFor(channel, root);
        Assert.Equal(workId, bound);
        Assert.Equal(botPost, parent);
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId.Value, "provider.input"));

        // The shared exchange cap: the echo row already spent it.
        await fixture.Db.SeedAdminConfig("loop-budget", """{"maxExchangesPerConversation": 1, "windowMinutes": 30}""");

        // Voice two (human reply): stopped at the budget — the arm never runs.
        var stoppedDelivery = $"post-{WalkKit.Leaf()}";
        var stopped = await KernelOps.PostJsonAsync(linkA, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "mattermost",
                stoppedDelivery, channel, root, "acct-1", "wake up"),
            HttpStatusCode.Accepted);
        Assert.Equal("loop-budget:stop:mattermost",
            stopped.GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal("loop-budget:stop:mattermost",
            await fixture.Db.ProviderEventJudgment(stoppedDelivery));

        // ONE stop event: the provider's redelivery of the same delivery is
        // a duplicate that reads the saved judgment — 200 with the stored
        // verdict, no second stop rides the ledger, and the Work still heard
        // nothing more.
        using var redelivery = await linkA.PostAsync("/api/link/v1/events",
            WalkKit.Content(WalkKit.ProviderEvent("valid/event.provider.mention.json", "mattermost",
                stoppedDelivery, channel, root, "acct-1", "wake up")));
        Assert.Equal(HttpStatusCode.OK, redelivery.StatusCode);
        var again = await redelivery.Content.ReadJsonAsync();
        Assert.Equal("loop-budget:stop:mattermost",
            again.GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal(1, await fixture.Db.ProviderEventCount("mattermost", stoppedDelivery));
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId.Value, "provider.input"));
    }
}
