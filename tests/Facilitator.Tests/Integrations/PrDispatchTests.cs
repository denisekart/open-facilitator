using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Artifacts;

using Facilitator.Tests.Infrastructure;
using Facilitator.Tests.Results;

namespace Facilitator.Tests.Integrations;

/// <summary>
/// The completed-manifest reader + head-branch resolver. A
/// recorded manifest whose assignment rode a provider-configured
/// PrRequestCapability (while the plane is off) dispatches its <c>structuredResult.prRequest</c> as a
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

    /// <summary>Phase 5: the assign-rule lane endpoint the boot importer
    /// bakes into the imported row — class-stable so the plane's funnel
    /// preference matches every test's lane fleet.</summary>
    internal const string CodingRuleEndpoint = "gw-coding-rule";
    private const string Head = "ofac/aaaaaaaa-1234";

    private readonly List<KernelTestFactory> _factories = [];

    /// <summary>Set per test before <see cref="CodingPlaneAsync"/>: boots
    /// the workflow runtime so the importer's enabled scoped copy makes
    /// the completed arm defer to the plane (the swap posture — assigns
    /// ride the routing funnel with the flow's command ids).</summary>
    private bool PlaneOn { get; set; }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // A leaked live fleet would join a later test's candidate set.
        await fixture.Db.PurgeFleets("gw-coding-");
        await fixture.Db.PurgeFleets("gw-global-");
        await fixture.Db.PurgeFleets("man-");
        await fixture.Db.PurgeFleets("gw-vcs-");
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
        PlaneOn = true;
        var (adapter, worker, codingEndpoint, _, workId, factory, vcsEndpoint) = await CodingPlaneAsync();
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

        // The swap posture: the arm DEFERRED; the plane ran the flow and
        // the funnel produced the assign — command id named by the flow's
        // identity laws (the deterministic chain event id -> trigger row ->
        // instance -> step effect), asserted against recomputation.
        var createCommand = await RoutingHarness.DrainUntilAsync(
            factory, () => PlaneEffectCommandAsync(envelope, "pr-create"), "plane create leg")
            + ""; // the log carries the effect id; the chain is the assertion
        Assert.StartsWith($"wf-{PlaneInstance(envelope):N}:pr-create:", createCommand);
        var dispatch = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(createCommand), "plane create assign");
        using var doc = JsonDocument.Parse(dispatch!);
        Assert.Equal("org.facilitator.command.work.assign.v1", doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("vcs.change_request.create", data.GetProperty("capabilityId").GetString());
        var inputs = data.GetProperty("inputs");
        Assert.Equal(Repo, inputs.GetProperty("repo").GetString());
        Assert.Equal(head, inputs.GetProperty("head").GetString());
        Assert.Equal("main", inputs.GetProperty("base").GetString());
        Assert.Equal("ofac change", inputs.GetProperty("title").GetString());
        Assert.Contains($"link.{vcsEndpoint}", await fixture.Db.OutboxRecipients(workId));

        // The pending-PR marker stands keyed by the head branch (1e input) —
        // parked by the plane's emit, not the arm.
        var marker = await RoutingHarness.DrainUntilAsync(
            factory,
            async () => (await fixture.Db.BindingFor("forgejo", Repo, "branch:" + head)).WorkId is { } w ? w.ToString() : null,
            "pending-PR marker");
        Assert.Equal(workId.ToString(), marker);

        // Replay of the same completed event: inbox duplicate; the plane
        // never saw the event twice (the trigger row converges on the
        // envelope id), so the effect stays single-shot.
        var replay = await adapter.PostAsync("/api/link/v1/events", Content(envelope));
        Assert.Equal("duplicate", (await JsonWire.ReadJsonAsync(replay.Content)).GetProperty("status").GetString());
        Assert.Single(await fixture.Db.OutboxRecipientsByCommand(createCommand));
    }

    [Fact]
    public async Task CommentRequestsEachDispatchACommentAssign()
    {
        PlaneOn = true;
        var (adapter, worker, _, _, workId, factory, vcsEndpoint) = await CodingPlaneAsync();
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
        var envelope = Completed(workId, wireAssignment, manifestId, artifactId, digest);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);

        // The fan-out leg logs its STEP command id
        // (`wf:<def>@1:<instance>:pr-comment:<seq>`); the element effects
        // derive: the effect-key law turns that into
        // `wf-<instance>:pr-comment:<seq>:<element>`.
        var fanStep = await RoutingHarness.DrainUntilAsync(
            factory, () => PlaneEffectCommandAsync(envelope, "pr-comment"), "plane fan-out leg");
        Assert.StartsWith($"wf:pr-on-manifest-forgejo@1:{PlaneInstance(envelope):N}:pr-comment:", fanStep);
        var fanBase = $"wf-{PlaneInstance(envelope):N}:pr-comment:{fanStep.Split(':')[4]}";
        foreach (var index in new[] { 0, 1 })
        {
            var dispatch = await RoutingHarness.DrainUntilAsync(
                factory,
                async () => await fixture.Db.OutboxEnvelopeByCommand($"{fanBase}:{index}") is { Length: > 0 } e ? e : null,
                $"plane comment assign {index} (fanBase {fanBase})");
            using var doc = JsonDocument.Parse(dispatch);
            Assert.Equal(
                "vcs.change_request.comment",
                doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        }

        var first = JsonDocument.Parse((await fixture.Db.OutboxEnvelopeByCommand($"{fanBase}:0"))!);
        Assert.Equal(
            "steer incorporated: rename the helper",
            first.RootElement.GetProperty("data").GetProperty("inputs").GetProperty("body").GetString());
    }

    [Fact]
    public async Task OpenedEventResolvesBranchMarkerOntoPrBinding()
    {
        PlaneOn = true;
        var (adapter, worker, _, _, workId, factory, vcsEndpoint) = await CodingPlaneAsync();
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
        var completedEnvelope = Completed(workId, wireAssignment, manifestId, artifactId, digest);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events", completedEnvelope, HttpStatusCode.Accepted);
        await DrainPlaneCreateAsync(factory, completedEnvelope);

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
        PlaneOn = true;
        // The connector opens the PR as the bot user, so the live resolver
        // input IS a bot-authored event: the echo-law exemption for
        // change_request opened/reopened keeps it on the CR arm — echo
        // suppression here would strand every bot-minted PR unbound.
        var (adapter, worker, _, _, workId, factory, vcsEndpoint) = await CodingPlaneAsync();
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
        var completedEnvelope = Completed(workId, wireAssignment, manifestId, artifactId, digest);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events", completedEnvelope, HttpStatusCode.Accepted);
        await DrainPlaneCreateAsync(factory, completedEnvelope);

        var pr = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(OpenedEvent($"dlv-{Guid.NewGuid():N}", Repo, pr, head, actor: "ofac-bot")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var receipt = await posted.Content.ReadJsonAsync();
        Assert.Equal($"pr-bound:{pr}", receipt.GetProperty("data").GetProperty("judgment").GetString());

        Assert.Equal(workId, (await fixture.Db.BindingFor("forgejo", Repo, pr)).WorkId);
    }

    [Fact]
    public async Task PlaneOffDispatchesOnTheArmWithDigestIds()
    {
        // The unchanged arm posture (plane runtime off — the gallery ships
        // disabled and the operator flips the knob): digest-keyed command
        // ids, the owning endpoint addressed directly, the marker parked
        // by the arm. The swap is opt-in; nothing moved under it.
        var (adapter, worker, codingEndpoint, _, workId, _, _) = await CodingPlaneAsync();
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
        var manifestId = "man-arm-" + workId.ToString("N")[..8];
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(
            ResultManifestTestKit.ManifestDoc(manifestId, structuredResult: structured));
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(worker, workId, bytes);
        await KernelOps.PostJsonAsync(adapter, "/api/link/v1/events",
            Completed(workId, wireAssignment, manifestId, artifactId, digest),
            HttpStatusCode.Accepted);

        var dispatch = await fixture.Db.OutboxEnvelopeByCommand($"pr-create:{digest}");
        Assert.NotNull(dispatch);
        Assert.Equal(
            "vcs.change_request.create",
            JsonDocument.Parse(dispatch!).RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        Assert.Contains($"link.{codingEndpoint}", await fixture.Db.OutboxRecipients(workId));
        Assert.Equal(workId, (await fixture.Db.BindingFor("forgejo", Repo, "branch:" + head)).WorkId);
    }

    [Fact]
    public async Task UnclaimedCapabilityKeepsManifestOpaque()
    {
        // The global (unlabeled) lane assigns analysis.summarize, which no
        // provider claims as PrRequestCapability: the manifest's prRequest
        // records, nothing dispatches.
        var (adapter, worker, codingEndpoint, laneEndpoint, workId, _, _) = await CodingPlaneAsync(label: "ofac:other");
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
    private async Task<(HttpClient Adapter, HttpClient Worker, string CodingEndpoint, string LaneEndpoint, Guid WorkId, KernelTestFactory Factory, string VcsEndpoint)> CodingPlaneAsync(
        string label = "ofac:coding")
    {
        var globalEndpoint = "gw-global-" + Guid.NewGuid().ToString("N")[..6];
        var fjEndpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var manEndpoint = "man-" + Guid.NewGuid().ToString("N")[..6];
        // The fleet that executes the VCS ops once the plane routes them
        // (the connector convention, mirrored as a plain capability fleet).
        var vcsEndpoint = "gw-vcs-" + Guid.NewGuid().ToString("N")[..6];
        // Phase 5: the rule ENDPOINT is class-stable — the boot importer
        // bakes it into the imported assign-rule row (once, at the first
        // boot of the class), and the plane's intent rides it as the
        // funnel preference. Per-test fleets re-seed on the same endpoint
        // (purged first), so preference-satisfied holds every run.
        var codingEndpoint = CodingRuleEndpoint;
        var settings = new Dictionary<string, string?>
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
        };
        if (PlaneOn)
        {
            settings["Facilitator:Workflows:Enabled"] = "true";
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        var authed = factory.CreateAuthedClient();
        var adapter = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(authed, fjEndpoint));
        var worker = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(authed, manEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

        if (PlaneOn)
        {
            // The VCS-op fleet the plane's routing intents resolve to (after
            // the factory booted: the schema is the factory's own migration).
            // Purge first: the funnel ranks equal candidates oldest-online
            // first, so a leaked sibling test's fleet would win the route.
            await fixture.Db.PurgeFleets("gw-vcs-");
            await FleetHarness.SeedFleetAsync(
                fixture.Db, vcsEndpoint, "team-alpha",
                "vcs.change_request.create", "vcs.change_request.comment");
        }

        // Lane fleet: the rule lane needs coding.implement live on the rule
        // endpoint, the global lane analysis.summarize on the global one.
        // team-alpha is the adapter's enrollment team (the work's team).
        await fixture.Db.PurgeFleets("gw-coding-");
        await fixture.Db.PurgeFleets("gw-global-");
        await FleetHarness.SeedFleetAsync(
            fixture.Db,
            label == "ofac:coding" ? codingEndpoint : globalEndpoint,
            "team-alpha",
            label == "ofac:coding" ? "coding.implement" : "analysis.summarize");

        var issue = new Random().Next(10000, 99999).ToString();
        var posted = await adapter.PostAsync("/api/link/v1/events",
            Content(IssueEvent($"dlv-{Guid.NewGuid():N}", Repo, issue, "alice", label)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        Guid? workId;
        if (PlaneOn)
        {
            // Phase 5: the labeled-issue creation is plane-owned (the
            // importer's live assign-rule-forgejo-1 copy), so the binding
            // lands when the plane runs — drain to it (the work id is the
            // plane's deterministic one; the assign family is unchanged).
            workId = Guid.Parse(await RoutingHarness.DrainUntilAsync(
                factory,
                async () => await fixture.Db.BindingWorkId("forgejo", Repo, issue) is { } w
                    ? w.ToString("D")
                    : null,
                "plane-created labeled-issue binding"));
        }
        else
        {
            workId = await fixture.Db.BindingWorkId("forgejo", Repo, issue);
        }

        Assert.NotNull(workId);
        await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand(
                label == "ofac:coding"
                    ? $"work-assign-coding:{workId.Value:N}"
                    : $"work-assign:{workId.Value:N}"),
            "interim assign effect");
        return (adapter, worker, codingEndpoint, label == "ofac:coding" ? codingEndpoint : globalEndpoint, workId.Value, factory, vcsEndpoint);
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

    /// <summary>The plane's instance identity for one completed envelope,
    /// recomputed through the identity laws: the trigger row is
    /// UUIDv5(def@ver&gt;event id), the instance UUIDv5(def@ver&gt;row). The
    /// effect command id (<c>wf-&lt;instance&gt;:&lt;step&gt;:&lt;seq&gt;</c>) then
    /// reads from the step log — <c>seq</c> counts the instance's legs, so
    /// the test asserts the CHAIN (event id → deterministic instance →
    /// logged effect), not a guessed counter.</summary>
    private static Guid PlaneInstance(string completedEnvelope)
    {
        var envelopeId = JsonDocument.Parse(completedEnvelope).RootElement.GetProperty("id").GetString()!;
        const string DefId = "pr-on-manifest-forgejo";
        return Facilitator.Kernel.Workflows.WorkflowIdentity.InstanceIdFor(
            DefId, 1, Facilitator.Kernel.Workflows.WorkflowIdentity.TriggerRowId(DefId, 1, envelopeId));
    }

    /// <summary>The step log's recorded effect command id for one plane leg
    /// (null until it lands — the drain probe's shape).</summary>
    private async Task<string?> PlaneEffectCommandAsync(string completedEnvelope, string stepId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        // The leg's OUTPUT names the effect command id the outbox row rode
        // (the log's own effect column is the STEP ledger id).
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT output::jsonb ->> 'commandId' FROM facilitator.workflow_step_log WHERE instance_id = $1 AND step_id = $2",
            conn);
        cmd.Parameters.AddWithValue(PlaneInstance(completedEnvelope));
        cmd.Parameters.AddWithValue(stepId);
        return await cmd.ExecuteScalarAsync() is string effect && effect.Length > 0 ? effect : null;
    }

    /// <summary>Run the plane to the marker: drains until the flow's
    /// create-assign effect exists (sink + funnel ride the same drains).</summary>
    private Task<string> DrainPlaneCreateAsync(KernelTestFactory factory, string completedEnvelope) =>
        RoutingHarness.DrainUntilAsync(
            factory,
            () => PlaneEffectCommandAsync(completedEnvelope, "pr-create"),
            "plane create leg");

    /// <summary>The completed-with-ref fixture rewritten: the assignmentId
    /// rides the ASSIGN envelope's wire value verbatim (id-token free form),
    /// and the generation stamp rides the assignment's current generation —
    /// the fence refuses a terminal event from any other generation.</summary>
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
        // The adapter-normalized field: the kernel reads this, never the payload.
        data["labels"] = new JsonArray(label);
        return doc.ToJsonString();
    }

    /// <summary>The change_request opened fixture rewritten for a fresh PR
    /// carrying the normalized <c>headRef</c>. The
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
        // The adapter-normalized field: the kernel reads this, never the payload.
        data["headRef"] = headRef;
        return doc.ToJsonString();
    }
}
