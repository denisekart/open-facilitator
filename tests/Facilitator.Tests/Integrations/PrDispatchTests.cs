using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Artifacts;
using Facilitator.Tests.Infrastructure;
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
/// keep the manifest opaque.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class PrDispatchTests(PostgresFixture fixture) : IDisposable
{
    private const string Repo = "demo/widgets";
    private const string Head = "ofac/aaaaaaaa-1234";

    private readonly List<KernelTestFactory> _factories = [];

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
        var (adapter, worker, codingEndpoint, workId) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId(workId);
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
        var (adapter, worker, _, workId) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId(workId);

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
        var (adapter, worker, _, workId) = await CodingPlaneAsync();
        var wireAssignment = WireAssignmentId(workId);
        var head = "ofac/" + workId.ToString("N")[..12];

        // Park the marker through the real leg (1d).
        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo, ["head"] = head, ["base"] = "main", ["title"] = "t", ["body"] = "b",
            },
        };
        var manifestId = "man-rs-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        // The provider's pull_request opened event: number 231 (fresh), head
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
    public async Task UnclaimedCapabilityKeepsManifestOpaque()
    {
        // The global (unlabeled) lane assigns analysis.summarize, which no
        // provider claims as PrRequestCapability: the manifest's prRequest
        // records, nothing dispatches.
        var (adapter, worker, codingEndpoint, workId) = await CodingPlaneAsync(label: "ofac:other");
        var wireAssignment = WireAssignmentId($"work-assign:{workId}");

        var structured = new JsonObject
        {
            ["prRequest"] = new JsonObject
            {
                ["repo"] = Repo, ["head"] = Head, ["base"] = "main", ["title"] = "t", ["body"] = "b",
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
        Assert.DoesNotContain("branch:" + Head, await fixture.Db.BindingRoots("forgejo", Repo));
    }

    // ---- plane ------------------------------------------------------------

    /// <summary>A forgejo plane with the coding rule + claimed capability, an
    /// adapter link client, a mode-B worker link client (manifest upload +
    /// completed events), and one Work armed through a labeled issue event.</summary>
    private async Task<(HttpClient Adapter, HttpClient Worker, string CodingEndpoint, Guid WorkId)> CodingPlaneAsync(string label = "ofac:coding")
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var fjEndpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var manEndpoint = "man-" + Guid.NewGuid().ToString("N")[..6];
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
            {
                ["Providers:Forgejo:Enabled"] = "true",
                ["Providers:Forgejo:EndpointId"] = fjEndpoint,
                ["Providers:Forgejo:Tenant"] = "git.localhost",
                ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
                ["Providers:Forgejo:BotUserId"] = "ofac-bot",
                ["Providers:Forgejo:AssignEndpointId"] = "gw-global-unused",
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

        var issue = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(IssueEvent($"dlv-{Guid.NewGuid():N}", Repo, issue, "alice", label)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        Assert.NotNull(workId);
        return (adapter, worker, codingEndpoint, workId.Value);
    }


    /// <summary>The wire assignmentId of the work's assign command.</summary>
    private string WireAssignmentId(Guid workId) =>
        WireAssignmentId($"work-assign-coding:{workId}");

    private string WireAssignmentId(string commandId)
    {
        var envelope = fixture.Db.OutboxEnvelopeByCommand(commandId).GetAwaiter().GetResult();
        Assert.NotNull(envelope);
        return JsonDocument.Parse(envelope).RootElement
            .GetProperty("data").GetProperty("assignmentId").GetString()!;
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The completed-with-ref fixture rewritten: the assignmentId
    /// rides the ASSIGN envelope's wire value verbatim (id-token free form).</summary>
    private static string Completed(Guid workId, string wireAssignmentId, string manifestId, Guid manifestArtifactId, string resultDigest)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.completed.with-manifest-ref.json"))!.AsObject();
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["subject"] = $"work/{workId}/assignment/{wireAssignmentId}";
        var data = envelope["data"]!.AsObject();
        data["workId"] = workId.ToString();
        data["assignmentId"] = wireAssignmentId;
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
    /// whose verbatim payload carries <c>pull_request.head.ref</c>.</summary>
    private static string OpenedEvent(string deliveryId, string repo, string number, string headRef)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.change_request.opened.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        doc["subject"] = $"provider/forgejo/pr-{number}";
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["pull_request"] = new JsonObject
        {
            ["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
            ["head"] = new JsonObject { ["ref"] = headRef },
        };
        return doc.ToJsonString();
    }
}
