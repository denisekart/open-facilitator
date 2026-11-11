using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// E4 review fork across the seam: a review payload injected at the kernel
/// sink queues <c>command.provider.webhook.v1> exactly once (the verbatim-
/// bytes law), the normalized <c>event.provider.review.v1</c> fed back the
/// way the adapter does lands a <c>review.decision</c> timeline row on the
/// bound terminal work, and the terminal law forks a successor with the
/// <c>successor</c> link while the conversation binding follows the live
/// item. Before this walk no single test asserted sink → normalized review
/// → decision row → fork as one chain.
/// Segments: WebhookSinkTests + ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt
/// (+ ReviewOnCompletedWorkForksSuccessor).
/// </summary>
[Collection("postgres:walks")]
public sealed class ReviewForkWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SinkReviewYieldsDecisionRowAndForkedSuccessor()
    {
        var endpoint = "gw-e4-" + WalkKit.Leaf();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
        });
        var kernel = factory.CreateAuthedClient();
        var http = factory.CreateClient();
        var link = factory.LinkClient(await WalkKit.EnrollAsync(kernel, endpoint));
        await fixture.Db.SeedProviderIdentity("forgejo", "git.localhost", "acct-1", "subject-1");
        var repo = $"repo-{WalkKit.Leaf()}";
        var pr = $"pr-{WalkKit.Leaf()}";

        // The conversation forms around the PR (the review's target).
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "forgejo", "git.localhost",
                $"dlv-{WalkKit.Leaf()}", repo, pr, "acct-1", "review this branch"),
            HttpStatusCode.Accepted);
        var workId = await fixture.Db.BindingWorkId("forgejo", repo, pr);
        Assert.NotNull(workId);
        await KernelOps.TransitionAsync(kernel, workId.Value, "planning", 1);
        await KernelOps.TransitionAsync(kernel, workId.Value, "cancelled", 2);

        // Leg one: the raw review payload at the sink — verbatim bytes +
        // headers queue the fan-out command, deduped by body hash.
        var raw = """{"action":"submitted","state":"changes_requested","review":{"body":"guard the null case"}}""";
        var commandId = "webhook:forgejo:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var posted = await KernelOps.PostJsonAsync(http, "/api/ingress/webhooks/forgejo", raw,
            HttpStatusCode.Accepted);
        Assert.Equal("queued", posted.GetProperty("status").GetString());
        Assert.Equal(commandId, posted.GetProperty("commandId").GetString());
        var envelopeJson = await fixture.Db.OutboxEnvelopeByCommand(commandId);
        Assert.NotNull(envelopeJson);
        Assert.Equal("org.facilitator.command.provider.webhook.v1",
            JsonDocument.Parse(envelopeJson).RootElement.GetProperty("type").GetString());
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipientsByCommand(commandId));

        // Leg two: the adapter's normalized review fed back the way the
        // webhook-receiver seam does (provider is data — the canonical
        // forgejo review fixture rewritten with fresh identity).
        var review = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.review.submitted.json"))!.AsObject();
        review["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = review["data"]!.AsObject();
        data["providerDeliveryId"] = $"rev-{WalkKit.Leaf()}";
        data["actorProviderAccountId"] = "acct-1";
        var reference = data["reference"]!.AsObject();
        reference["tenantOrOrganizationId"] = "git.localhost";
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = pr;
        using var submitted = await link.PostAsync("/api/link/v1/events",
            new StringContent(review.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var receipt = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync()).RootElement;
        var judgment = receipt.GetProperty("data").GetProperty("judgment").GetString()!;

        // The seam: the decision row on the terminal original, the forked
        // successor named by the judgment, the link row, and the binding
        // following the live item (terminal law).
        Assert.StartsWith("work/", judgment);
        var successor = Guid.Parse(judgment.AsSpan("work/".Length, 36));
        Assert.NotEqual(workId, successor);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "review.decision"));
        Assert.Equal(1, await fixture.Db.WorkLinkCount(workId.Value, successor, "successor"));
        var (bound, _) = await fixture.Db.BindingFor("forgejo", repo, pr);
        Assert.Equal(successor, bound);
    }
}
