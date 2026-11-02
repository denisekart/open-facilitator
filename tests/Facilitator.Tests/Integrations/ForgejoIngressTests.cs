using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace Facilitator.Tests.Integrations;

/// <summary>
/// Forgejo arms over the link HTTP path on the registry-based plane:
/// a labeled issue (workTrigger true) creates exactly one bound Work — the
/// redelivery answers <c>duplicate</c> with the stored judgment and zero new
/// rows; an unbound unlabeled event makes no Work; a comment on a bound issue
/// appends exactly one <c>provider.input</c>; a PR review steers the bound
/// Work (version bump) post-commit; an unbound PR judges
/// <c>observe:unbound</c> with zero side effects; a CI failure note carries
/// the status token. Per-provider addressing is the regression oracle: the
/// ack/assign/completion rows ride <c>link.&lt;forgejo endpoint&gt;</c>, and a
/// forgejo-family event against a mattermost-only composition refuses
/// fail-closed.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ForgejoIngressTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";
    private const string Repo = "demo/widgets";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // A leaked live fleet would join a later test's candidate set: the
        // funnel routes preference-null intents to ANY live fleet with the
        // interim capability on the work's team.
        await fixture.Db.PurgeFleets("gw-assign-");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task LabeledIssueCreatesWorkOnceAndDuplicateReturnsJudgment()
    {
        var assignEndpoint = "gw-assign-" + Guid.NewGuid().ToString("N")[..6];
        var (factory, link, _, endpoint) = await EnabledForgejoPlane(assignEndpointId: assignEndpoint);
        var issue = IssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var first = await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", delivery, Repo, issue, "alice")));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var receipt = await first.Content.ReadJsonAsync();
        Assert.Equal("accepted", receipt.GetProperty("status").GetString());

        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);
        var work = await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("open", work.GetProperty("state").GetString());
        Assert.Equal(1, await fixture.Db.ProviderEventCount("forgejo", delivery));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "created"));
        Assert.Equal($"work/{workId}", await fixture.Db.ProviderEventJudgment(delivery));

        // Ack rides a reply comment to the FORGEJO adapter endpoint (decision 4).
        var ack = await fixture.Db.OutboxEnvelopeByCommand($"issue:{delivery}:ack");
        Assert.NotNull(ack);
        Assert.Contains("command.communication.reply.v1", ack);
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId.Value));

        // Interim assign through the routing funnel (decision 3, 22's shape,
        // 35's seam): the fleet for the lane is live, so drain until the
        // preference-satisfied effect row lands under the N-format key.
        await FleetHarness.SeedFleetAsync(fixture.Db, assignEndpoint, "team-alpha", "analysis.summarize");
        await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId.Value:N}"),
            "interim assign effect");
        var assign = await fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId.Value:N}");
        Assert.NotNull(assign);
        Assert.Contains("forgejo-interim", assign);

        // Redelivery: duplicate, saved judgment, zero new arm effects.
        var second = await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", delivery, Repo, issue, "alice")));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var dup = await second.Content.ReadJsonAsync();
        Assert.Equal("duplicate", dup.GetProperty("status").GetString());
        Assert.Equal($"work/{workId}", dup.GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal(1, await fixture.Db.ProviderEventCount("forgejo", delivery));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "created"));
    }

    [Fact]
    public async Task UnlabeledOpenedIssueMakesNoWork()
    {
        var (_, link, _, _) = await EnabledForgejoPlane();
        var issue = IssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var response = await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.opened.json", delivery, Repo, issue, "alice")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // workTrigger false ⇒ observation only: no binding, no Work.
        Assert.Null(await fixture.Db.BindingWorkId("forgejo", Repo, issue));
        Assert.Equal("observe:opened", await fixture.Db.ProviderEventJudgment(delivery));
    }

    [Fact]
    public async Task CommentOnBoundIssueAppendsExactlyOneInput()
    {
        var (factory, link, _, _) = await EnabledForgejoPlane();
        var issue = IssueNumber();
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", $"dlv-{Guid.NewGuid():N}", Repo, issue, "alice")));
        var workId = (await fixture.Db.BindingWorkId("forgejo", Repo, issue))!.Value;

        var delivery = $"dlv-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.commented.json", delivery, Repo, issue, "bob", text: "also cover the timeout path")));

        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "provider.input"));
        Assert.Equal($"work/{workId}:input", await fixture.Db.ProviderEventJudgment(delivery));
        var note = Assert.Single(await fixture.Db.TimelineDetail(workId, "provider.input"));
        Assert.Contains("forgejo post", note);
        Assert.Contains("timeout path", note);
        // Input notes are timeline facts, not versioned decisions (22 law).
        Assert.Equal(1, (await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK))
            .GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ReviewedPrOnBoundWorkSteersAndBumpsVersion()
    {
        var (factory, link, _, _) = await EnabledForgejoPlane();
        var pr = IssueNumber();
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", $"dlv-{Guid.NewGuid():N}", Repo, pr, "alice")));
        var workId = (await fixture.Db.BindingWorkId("forgejo", Repo, pr))!.Value;

        var delivery = $"dlv-{Guid.NewGuid():N}";
        var response = await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.change_request.reviewed.json", delivery, Repo, pr, "reviewer",
            text: "approve with the retry clause reworded")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal($"steered:{workId}", await fixture.Db.ProviderEventJudgment(delivery));
        var work = await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal(2, work.GetProperty("version").GetInt64());
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "steer"));
    }

    [Fact]
    public async Task UnboundPrHasZeroSideEffects()
    {
        var (factory, link, _, _) = await EnabledForgejoPlane();
        var pr = IssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var response = await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.change_request.reviewed.json", delivery, Repo, pr, "reviewer")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal("observe:unbound", await fixture.Db.ProviderEventJudgment(delivery));
        Assert.Null(await fixture.Db.BindingWorkId("forgejo", Repo, pr));
        await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), "/api/works?limit=1000", HttpStatusCode.OK);
    }

    [Fact]
    public async Task CiFailureOnBoundIssueAnnotesWork()
    {
        var (factory, link, _, _) = await EnabledForgejoPlane();
        var issue = IssueNumber();
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", $"dlv-{Guid.NewGuid():N}", Repo, issue, "alice")));
        var workId = (await fixture.Db.BindingWorkId("forgejo", Repo, issue))!.Value;

        var delivery = $"dlv-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.pipeline.failure.json", delivery, Repo, issue, "ci-bot")));

        Assert.Equal($"work/{workId}:input", await fixture.Db.ProviderEventJudgment(delivery));
        var note = Assert.Single(await fixture.Db.TimelineDetail(workId, "provider.input"));
        Assert.Contains("failure", note);
        await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK);
    }

    [Fact]
    public async Task TerminalTransitionCompletesToForgejoEndpoint()
    {
        // The misaddressing regression (verification §): a forgejo-bound Work's
        // completion reply must ride link.<forgejo endpoint>, not a singleton.
        var (factory, link, _, endpoint) = await EnabledForgejoPlane(mattermost: false);
        var issue = IssueNumber();
        await link.PostAsync("/api/link/v1/events", Content(ForgejoEvent(
            "valid/event.provider.issue.labeled.json", $"dlv-{Guid.NewGuid():N}", Repo, issue, "alice")));
        var workId = (await fixture.Db.BindingWorkId("forgejo", Repo, issue))!.Value;

        await KernelOps.TransitionAsync(factory.CreateAuthedClient(), workId, "planning", 1);
        await KernelOps.TransitionAsync(factory.CreateAuthedClient(), workId, "cancelled", 2);

        var envelopes = await fixture.Db.OutboxEnvelopes(workId);
        var replies = envelopes.Where(e => e.Contains("command.communication.reply.v1", StringComparison.Ordinal)).ToList();
        var completion = Assert.Single(replies, e => e.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains(Repo, completion);
        var recipients = await fixture.Db.OutboxRecipients(workId);
        Assert.Contains($"link.{endpoint}", recipients);
        // The misaddressing oracle: every LINK-addressed row (reply/completion)
        // rides the forgejo endpoint; kernel.log progress is kernel-internal.
        Assert.All(recipients.Where(r => r.StartsWith("link.", StringComparison.Ordinal)),
            r => Assert.Equal($"link.{endpoint}", r));
    }

    [Fact]
    public async Task ForgejoEventRefusedOnMattermostOnlyPlane()
    {
        // Registry gate per-provider: the family passes the class screen, but a
        // composition without the forgejo section refuses fail-closed.
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), "gw-mm-only"));

        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = ForgejoEvent("valid/event.provider.issue.labeled.json", delivery, Repo, IssueNumber(), "alice");
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());
        Assert.Equal(0, await fixture.Db.ProviderEventCount("forgejo", delivery));
    }

    [Fact]
    public async Task ProviderEventFromWrongEndpointRefused()
    {
        // Sender binding: an enabled forgejo section, but the sending link
        // endpoint is NOT the section's EndpointId — typed refusal + failed
        // audit row, zero provider_events rows.
        var (factory, _, _, _) = await EnabledForgejoPlane();
        var impostor = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(
            factory.CreateAuthedClient(), "gw-impostor-" + Guid.NewGuid().ToString("N")[..6]));

        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = WithId(ForgejoEvent("valid/event.provider.issue.labeled.json", delivery, Repo, IssueNumber(), "alice"), "evt-mismatch-" + delivery);
        using var response = await impostor.PostAsync("/api/link/v1/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());
        Assert.Contains("does not match provider", problem.GetProperty("detail").GetString());
        Assert.True(await fixture.Db.InboxFailedFor("evt-mismatch-" + delivery), "failed audit row");
        Assert.Equal(0, await fixture.Db.ProviderEventCount("forgejo", delivery));
    }

    [Fact]
    public async Task ProviderTokenMismatchAgainstReferenceRefused()
    {
        // data.provider MUST equal reference.provider: a wrong-claimed token
        // from the right endpoint refuses typed unsupported + failed audit row.
        var (_, link, _, _) = await EnabledForgejoPlane();
        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = WithId(ForgejoEvent("valid/event.provider.issue.labeled.json", delivery, Repo, IssueNumber(), "alice"), "evt-mismatch-" + delivery);
        envelope = envelope.Replace("\"provider\":\"forgejo\",\"providerDeliveryId\"", "\"provider\":\"gitlab-fake\",\"providerDeliveryId\"");
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());
        Assert.True(await fixture.Db.InboxFailedFor("evt-mismatch-" + delivery), "failed audit row");
        Assert.Equal(0, await fixture.Db.ProviderEventCount("forgejo", delivery));
    }

    // ---- helpers ------------------------------------------------------------

    private static string IssueNumber() => Random.Shared.Next(1000, 999_999).ToString();

    private async Task<(KernelTestFactory Factory, HttpClient Link, string Tenant, string Endpoint)> EnabledForgejoPlane(
        string? assignEndpointId = null, bool mattermost = true)
    {
        var endpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = "git.localhost";
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = tenant,
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
        };
        if (assignEndpointId is not null)
        {
            settings["Providers:Forgejo:AssignEndpointId"] = assignEndpointId;
        }

        if (mattermost)
        {
            // Both families live at once: the registry keys them separately.
            settings["Providers:Mattermost:Enabled"] = "true";
            settings["Providers:Mattermost:EndpointId"] = "gw-mm-" + Guid.NewGuid().ToString("N")[..8];
            settings["Providers:Mattermost:Tenant"] = "demo-team";
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (factory, link, tenant, endpoint);
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    private static string ForgejoEvent(
        string fixtureName, string deliveryId, string repo, string number, string actor, string? text = null)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture(fixtureName))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        reference["authorProviderAccountId"] = actor;
        if (text is not null)
        {
            data["text"] = text;
        }

        return doc.ToJsonString();
    }

    /// <summary>Pin the envelope id so the failed-audit probe can find the row.</summary>
    private static string WithId(string envelope, string id)
    {
        var doc = JsonNode.Parse(envelope)!.AsObject();
        doc["id"] = id;
        return doc.ToJsonString();
    }
}
