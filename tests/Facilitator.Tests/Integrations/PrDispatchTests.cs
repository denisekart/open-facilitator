using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Artifacts;

using Facilitator.Tests.Results;

namespace Facilitator.Tests.Integrations;

/// <summary>
/// Plan 24(1d/1e): the completed-manifest reader + head-branch resolver. A
/// recorded manifest whose assignment rode a provider-configured
/// PrRequestCapability dispatches its <c>structuredResult.prRequest</c> as a
/// <c>vcs.change_request.create</c> assign to the assignment's owning
/// endpoint (digest-keyed ⇒ replay single-shot) and parks the
/// <c>branch:&lt;head&gt;</c> marker binding; <c>commentRequests[]</c> each
/// dispatch a <c>vcs.change_request.comment</c>. A later pull_request
/// <c>opened</c> whose payload head ref matches the marker resolves it onto a
/// PR-number-keyed binding. Assignments whose capability no provider claims
/// keep the manifest opaque. The interim assign rides the routing funnel, so
/// each plane seeds a live fleet for its lane and awaits the effect row
/// before the manifest leg.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class PrDispatchTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Repo = "demo/widgets";
    private const string Head = "ofac/aaaaaaaa-1234";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // A leaked live fleet would join a later test's candidate set.
        await fixture.Db.PurgeFleets("gw-coding-");
        await fixture.Db.PurgeFleets("gw-global-");
        await fixture.Db.PurgeFleets("man-");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task PrRequestDispatchesCreateAndParksBranchMarker()
    {
        var (adapter, worker, codingEndpoint, _, workId, _) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId($"work-assign-coding:{workId:N}");
        var head = "ofac/" + workId.ToString("N")[..12];

        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo,
                ["head"] = head,
                ["base"] = "main",
                ["title"] = "ofac change",
                ["body"] = "Closes #" + workId.ToString()[..4],
            },
        };
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc("man-pr1-" + workId.ToString("N")[..8], structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        var envelope = Completed(workId, wireAssignment, "man-pr1-" + workId.ToString("N")[..8], artifactId, digest);

        var ack = await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());

        var dispatch = await fixture.Db.OutboxEnvelopeByCommand($"pr-create:{digest}");
        Assert.NotNull(dispatch);
        using var doc = JsonDocument.Parse(dispatch);
        Assert.Equal("org.facilitator.command.work.assign.v1", doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("vcs.change_request.create", data.GetProperty("capabilityId").GetString());
        var inputs = data.GetProperty("inputs");
        Assert.Equal(Repo, inputs.GetProperty("repo").GetString());
        Assert.Equal(head, inputs.GetProperty("head").GetString());
        Assert.Equal("main", inputs.GetProperty("base").GetString());
        Assert.Equal("ofac change", inputs.GetProperty("title").GetString());
        Assert.Contains($"link.{codingEndpoint}", await fixture.Db.OutboxRecipients(workId));

        // The pending-PR marker stands keyed by the head branch (1e input).
        var marker = await fixture.Db.BindingFor("forgejo", Repo, "branch:" + head);
        Assert.Equal(workId, marker.WorkId);

        // Replay of the same completed event: inbox duplicate; the digest-
        // keyed command id keeps the dispatch single-shot (probe asserts a
        // single row).
        var replay = await adapter.PostAsync("/api/link/v1/events", Content(envelope));
        Assert.Equal("duplicate", (await JsonWire.ReadJsonAsync(replay.Content)).GetProperty("status").GetString());
        Assert.NotNull(await fixture.Db.OutboxEnvelopeByCommand($"pr-create:{digest}"));
    }

    [Fact]
    public async Task CommentRequestsEachDispatchACommentAssign()
    {
        var (adapter, worker, _, _, workId, _) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId($"work-assign-coding:{workId:N}");

        var structured = new JsonObject
        {
            ["commentRequests"] = new JsonArray(
                new JsonObject { ["repo"] = Repo, ["issue"] = "7", ["body"] = "steer incorporated: rename the helper" },
                new JsonObject { ["repo"] = Repo, ["issue"] = "7", ["body"] = "second note" }),
        };
        var manifestId = "man-cm-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        foreach (var index in new[] { 0, 1 })
        {
            var dispatch = await fixture.Db.OutboxEnvelopeByCommand($"pr-comment:{digest}:{index}");
            Assert.NotNull(dispatch);
            using var doc = JsonDocument.Parse(dispatch);
            Assert.Equal(
                "vcs.change_request.comment",
                doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        }

        var first = JsonDocument.Parse((await fixture.Db.OutboxEnvelopeByCommand($"pr-comment:{digest}:0"))!);
        Assert.Equal(
            "steer incorporated: rename the helper",
            first.RootElement.GetProperty("data").GetProperty("inputs").GetProperty("body").GetString());
    }

    [Fact]
    public async Task OpenedEventResolvesBranchMarkerOntoPrBinding()
    {
        var (adapter, worker, _, _, workId, _) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId($"work-assign-coding:{workId:N}");
        var head = "ofac/" + workId.ToString("N")[..12];

        // Park the marker through the real leg (1d).
        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo,
                ["head"] = head,
                ["base"] = "main",
                ["title"] = "t",
                ["body"] = "b",
            },
        };
        var manifestId = "man-rs-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        // The provider's pull_request opened event: number fresh, head
        // ref matching the marker.
        var pr = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(OpenedEvent($"dlv-{Guid.NewGuid():N}", Repo, pr, head)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var resolved = await fixture.Db.BindingFor("forgejo", Repo, pr);
        Assert.Equal(workId, resolved.WorkId);
        // The marker row stands: re-open reconciliation reads it unchanged.
        Assert.Equal(workId, (await fixture.Db.BindingFor("forgejo", Repo, "branch:" + head)).WorkId);
    }

    [Fact]
    public async Task BotOpenedEventResolvesMarkerDespiteEchoLaw()
    {
        // The connector opens the PR as the bot user, so the live resolver
        // input IS a bot-authored event: the echo-law exemption for
        // change_request opened/reopened keeps it on the CR arm — echo
        // suppression here would strand every bot-minted PR unbound.
        var (adapter, worker, _, _, workId, _) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId($"work-assign-coding:{workId:N}");
        var head = "ofac/" + workId.ToString("N")[..12];

        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo,
                ["head"] = head,
                ["base"] = "main",
                ["title"] = "t",
                ["body"] = "b",
            },
        };
        var manifestId = "man-rb-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        var pr = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(OpenedEvent($"dlv-{Guid.NewGuid():N}", Repo, pr, head, actor: "ofac-bot")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var receipt = await posted.Content.ReadJsonAsync();
        Assert.Equal($"pr-bound:{pr}", receipt.GetProperty("data").GetProperty("judgment").GetString());

        Assert.Equal(workId, (await fixture.Db.BindingFor("forgejo", Repo, pr)).WorkId);
    }

    [Fact]
    public async Task UnclaimedCapabilityKeepsManifestOpaque()
    {
        // The global (unlabeled) lane assigns analysis.summarize, which no
        // provider claims as PrRequestCapability: the manifest's prRequest
        // records, nothing dispatches.
        var (adapter, worker, codingEndpoint, laneEndpoint, workId, _) = await CodingPlaneAsync(label: "ofac:other");
        var wireAssignment = WireAssignmentId($"work-assign:{workId:N}");

        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo,
                ["head"] = Head,
                ["base"] = "main",
                ["title"] = "t",
                ["body"] = "b",
            },
        };
        var manifestId = "man-oc-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        Assert.Equal(1, await fixture.Db.ManifestRowCountByDigest(digest));
        Assert.DoesNotContain($"link.{codingEndpoint}", await fixture.Db.OutboxRecipients(workId));
        Assert.Contains($"link.{laneEndpoint}", await fixture.Db.OutboxRecipients(workId));
        Assert.DoesNotContain("branch:" + Head, await fixture.Db.BindingRoots("forgejo", Repo));
    }

    // ---- plane ------------------------------------------------------------

    /// <summary>A forgejo plane with the coding rule + claimed capability, an
    /// adapter link client, a mode-B worker link client (manifest upload +
    /// completed events), and one Work armed through a labeled issue event.
    /// The lane's fleet is live and its routing effect is awaited, so the
    /// wire assignmentId (the envelope's) exists when this returns.</summary>
    private async Task<(HttpClient Adapter, HttpClient Worker, string CodingEndpoint, string LaneEndpoint, Guid WorkId, KernelTestFactory Factory)> CodingPlaneAsync(string label = "ofac:coding")
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var fjEndpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var manEndpoint = "man-" + Guid.NewGuid().ToString("N")[..6];
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = fjEndpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
            ["Providers:Forgejo:AssignEndpointId"] = globalEndpoint,
            ["Providers:Forgejo:AssignRules:0:Label"] = "ofac:coding",
            ["Providers:Forgejo:AssignRules:0:CapabilityId"] = "coding.implement",
            ["Providers:Forgejo:AssignRules:0:EndpointId"] = codingEndpoint,
            ["Providers:Forgejo:PrRequestCapability"] = "coding.implement",
            ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
            ["Link:WorkloadIdentities:2:EndpointId"] = manEndpoint,
            ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
            ["Link:WorkloadIdentities:2:Role"] = "child",
            ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "true",
        });
        _factories.Add(factory);
        var authed = factory.CreateAuthedClient();
        var adapter = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(authed, fjEndpoint));
        var worker = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(authed, manEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

        // Lane fleet: the rule lane needs coding.implement live on the rule
        // endpoint, the global lane analysis.summarize on the global one.
        // team-alpha is the adapter's enrollment team (the work's team).
        await FleetHarness.SeedFleetAsync(
            fixture.Db,
            label == "ofac:coding" ? codingEndpoint : globalEndpoint,
            "team-alpha",
            label == "ofac:coding" ? "coding.implement" : "analysis.summarize");

        var issue = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(IssueEvent($"dlv-{Guid.NewGuid():N}", Repo, issue, "alice", label)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);
        await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand(
                label == "ofac:coding"
                    ? $"work-assign-coding:{workId.Value:N}"
                    : $"work-assign:{workId.Value:N}"),
            "interim assign effect");
        return (adapter, worker, codingEndpoint, label == "ofac:coding" ? codingEndpoint : globalEndpoint, workId.Value, factory);
    }

    /// <summary>The wire assignmentId of an assign envelope (post-funnel, the
    /// envelope's data.assignmentId — the gateway binds on it).</summary>
    private string WireAssignmentId(string commandId)
    {
        var envelope = fixture.Db.OutboxEnvelopeByCommand(commandId).GetAwaiter().GetResult();
        Assert.NotNull(envelope);
        return JsonDocument.Parse(envelope).RootElement
            .GetProperty("data").GetProperty("assignmentId").GetString()!;
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The completed-with-ref fixture rewritten: the assignmentId
    /// rides the ASSIGN envelope's wire value verbatim (id-token free form),
    /// and the generation stamp rides the assignment's current generation —
    /// the §7.4 fence refuses a terminal event from any other generation.</summary>
    private static string Completed(Guid workId, string wireAssignmentId, string manifestId, Guid manifestArtifactId, string resultDigest)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.completed.with-manifest-ref.json"))!.AsObject();
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["subject"] = $"work/{workId}/assignment/{wireAssignmentId}";
        var data = envelope["data"]!.AsObject();
        data["workId"] = workId.ToString();
        data["assignmentId"] = wireAssignmentId;
        data["executionGeneration"] = 1;
        data["executionId"] = "exec-" + manifestId;
        data["resultManifest"]!.AsObject()["manifestId"] = manifestId;
        data["resultManifestRef"] = $"artifact://{workId}/{wireAssignmentId}/{manifestArtifactId}";
        data["resultDigest"] = resultDigest;
        return envelope.ToJsonString();
    }

    /// <summary>The forgejo labeled-issue fixture rewritten (same shape the
    /// assign-rule suite rides).</summary>
    private static string IssueEvent(string deliveryId, string repo, string number, string actor, string label)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.labeled.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        reference["authorProviderAccountId"] = actor;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["label"] = new JsonObject { ["name"] = label };
        payload["issue"] = new JsonObject
        {
            ["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
            ["labels"] = new JsonArray(new JsonObject { ["name"] = label }),
        };
        return doc.ToJsonString();
    }

    /// <summary>The change_request opened fixture rewritten for a fresh PR
    /// whose verbatim payload carries <c>pull_request.head.ref</c>. The
    /// <paramref name="actor"/> rides both actor fields (the kernel echo law
    /// keys on <c>actorProviderAccountId</c>).</summary>
    private static string OpenedEvent(string deliveryId, string repo, string number, string headRef, string actor = "alice")
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.change_request.opened.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        reference["authorProviderAccountId"] = actor;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["pull_request"] = new JsonObject { ["head"] = new JsonObject { ["ref"] = headRef } };
        return doc.ToJsonString();
    }
}
