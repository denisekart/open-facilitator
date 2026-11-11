using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Approvals;

/// <summary>
/// Step 3 (held actions + consent authority): the kernel stores the held
/// command BEFORE any notice goes out (persist-first law D3) and hands the
/// stored envelope back on consume — execution runs exactly what consent
/// covered, and the kernel's copy (not a gateway recollection) is the source.
/// Consent authority: the not-requester rule denies the requesting side as
/// sole consent, an ineligible deny never consumes, and a replay is told the
/// winner. The consent sweep nudges before expiry and parks the work LOUD
/// when consent lapses (C6). Chat consent leaves a paper trail (C7): a
/// tracker record through the provider plane, or a named disclosure.
/// </summary>
[Collection("postgres:work")]
public sealed class ConsentAuthorityTests(PostgresFixture fixture)
{
    private static readonly JsonNode DefaultAction = JsonNode.Parse(
        """{"operation":"publish","resource":"repository:service-api"}""")!;

    private static readonly JsonObject HeldCommand = JsonNode.Parse(
        """
        {
          "specversion": "1.0",
          "id": "cmd-held-1",
          "source": "urn:workspace:gw.test",
          "type": "org.facilitator.command.work.assign.v1",
          "subject": "work/00000000-0000-0000-0000-000000000000",
          "time": "2026-01-01T00:00:00Z",
          "datacontenttype": "application/json",
          "dataschema": "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
          "data": {
            "commandId": "cmd-held-1",
            "workId": "00000000-0000-0000-0000-000000000000",
            "targetId": "child-1",
            "capabilityId": "coding.implement",
            "expectedContextVersion": 1,
            "inputs": {"marker": "original"},
            "expectedDeliverables": [],
            "actorEvidenceRef": "workload:gw.test",
            "deadline": "2026-01-02T00:00:00Z",
            "assignmentId": "asg-held-1",
            "executionGeneration": 1
          }
        }
        """)!.AsObject();

    // ---- persist-first law D3 ------------------------------------------------

    [Fact]
    public async Task HeldCommandStoredWithTheApprovalRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-s3-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "persist first");
        var approvalId = Guid.NewGuid();

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1),
                command: HeldCommand, requestedBy: "workload:gw.test", ruleId: "prod-publish"),
            HttpStatusCode.Accepted);

        // The 202 that lets a notice exist already implies the stored command:
        // held row + consent columns, same transaction, pending.
        var held = await fixture.Db.HeldAction(approvalId);
        Assert.NotNull(held);
        Assert.Equal("pending", held.State);
        Assert.Equal(endpoint, held.EndpointId);
        Assert.Equal("coding.implement", held.CapabilityId);
        Assert.Equal(
            "cmd-held-1",
            JsonDocument.Parse(held.Envelope).RootElement.GetProperty("id").GetString());
        // The approval row itself records pending under the declaring rule.
        Assert.Equal("pending", (await fixture.Db.Approval(approvalId))!.State);
    }

    [Fact]
    public async Task ConsumeReturnsTheKernelStoredEnvelope()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-s3-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume envelope");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1),
                command: HeldCommand, requestedBy: "workload:gw.test"),
            HttpStatusCode.Accepted);

        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        // Consume: in-band ack data carries the stored envelope verbatim.
        var consumed = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ConsumeEnvelope(approvalId), HttpStatusCode.Accepted);
        Assert.Equal("consumed", consumed.GetProperty("status").GetString());
        var envelope = consumed.GetProperty("data").GetProperty("envelope");
        Assert.Equal("cmd-held-1", envelope.GetProperty("id").GetString());
        Assert.Equal("coding.implement",
            envelope.GetProperty("data").GetProperty("capabilityId").GetString());

        // Single use: the same command again is a 409, no second envelope.
        using var replay = await link.PostAsync("/api/link/v1/events",
            new StringContent(ConsumeEnvelope(approvalId), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        var held = await fixture.Db.HeldAction(approvalId);
        Assert.NotNull(held);
        Assert.Equal("delivered", held.State);
    }

    [Fact]
    public async Task MutateThenConsumeReturnsTheMutatedStoredCopy()
    {
        // "Mutate then approve": whatever happens to the held bytes, the
        // kernel's copy is what consent covers — consume hands back the
        // mutated row, never the sender's original.
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-s3-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "mutate then approve");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1),
                command: HeldCommand, requestedBy: "workload:gw.test"),
            HttpStatusCode.Accepted);

        await fixture.Db.MutateHeldEnvelope(approvalId, """{"marker":"mutated"}""");

        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);
        var consumed = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ConsumeEnvelope(approvalId), HttpStatusCode.Accepted);
        Assert.Equal("cmd-held-1",
            consumed.GetProperty("data").GetProperty("envelope").GetProperty("id").GetString());
        // The kernel's (mutated) copy is what execution would run.
        Assert.Equal("mutated", JsonDocument.Parse(
                consumed.GetProperty("data").GetProperty("envelope").ToString())
            .RootElement.GetProperty("data").GetProperty("inputs").GetProperty("marker").GetString());
    }

    // ---- consent authority ----------------------------------------------------

    [Fact]
    public async Task OutsiderDenyDoesNotConsumeTheApproval()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "outsider deny");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromMinutes(30)),
            HttpStatusCode.Accepted);

        // Carol is not an eligible actor: 403, and the row stays pending — a
        // deny from a stranger must not spend the approval.
        var carol = await CreateUserClientAsync(factory, "carol");
        using var denied = await carol.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"denied"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("pending", (await fixture.Db.Approval(approvalId))!.State);

        // The entitled denial consumes exactly once.
        var alice = await CreateUserClientAsync(factory, "alice");
        var responded = await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"denied"}""", HttpStatusCode.OK);
        Assert.Equal("denied", responded.GetProperty("state").GetString());
        Assert.Equal("denied", (await fixture.Db.Approval(approvalId))!.State);
    }

    [Fact]
    public async Task TwoRespondersOneDecidedAndTheLoserSeesWhoDecided()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "two responders");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice", "user:bob"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);

        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        // Bob is a plain team member (no operator role): his identity rides
        // minted evidence on the ungated wire plane. His replay: 409, and the
        // ledger names the winner for the reply.
        var bobActor = await KeycloakFixture.ActorOfAsync("bob", "bob");
        await fixture.Db.SeedEvidence("ev-two-" + approvalId.ToString("N"), bobActor,
            DateTime.UtcNow.AddMinutes(5), null);
        using var replay = await kernel.PostAsync("/api/ingress/events",
            new StringContent(RespondEnvelope(approvalId, "denied", $"kernel:ev-two-{approvalId:N}"),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var problem = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-already-consumed",
            problem.GetProperty("type").GetString());

        var listed = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/approvals", HttpStatusCode.OK);
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal("approved", row.GetProperty("state").GetString());
        Assert.StartsWith("oidc:", row.GetProperty("decidedBy").GetString());
        _ = bobActor;
    }

    [Fact]
    public async Task NotRequesterRuleDeniesTheRequestingSideAsSoleConsent()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "not requester");
        var approvalId = Guid.NewGuid();
        var aliceActor = await CanonicalActorAsync("alice");

        // Alice's request needs consent she may not give herself, though a
        // different side can still decide.
        var bobActor = await KeycloakFixture.ActorOfAsync("bob", "bob");
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice", bobActor], TimeSpan.FromHours(1),
                requestedBy: aliceActor, ruleId: "prod-publish", notRequester: true),
            HttpStatusCode.Accepted);
        await fixture.Db.SeedEvidence("ev-nr-" + approvalId.ToString("N"), bobActor,
            DateTime.UtcNow.AddMinutes(5), null);

        var alice = await CreateUserClientAsync(factory, "alice");
        using var selfApproved = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, selfApproved.StatusCode);
        Assert.Equal("pending", (await fixture.Db.Approval(approvalId))!.State);

        // A different side decides — the rule gates the requester, not consent.
        var responded = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RespondEnvelope(approvalId, "approved", $"kernel:ev-nr-{approvalId:N}"), HttpStatusCode.OK);
        Assert.Equal("decided", responded.GetProperty("status").GetString());
        Assert.Equal("approved", (await fixture.Db.Approval(approvalId))!.State);
    }

    // ---- the consent sweep (C6) ----------------------------------------------

    [Fact]
    public async Task NudgeBeforeExpiryWritesTimelineAndStampsOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig("consent-policy", """{"nudgeBeforeMinutes":10,"expiresAfterMinutes":60}""");
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "nudge leg");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromMinutes(5)),
            HttpStatusCode.Accepted);

        var sweeper = factory.Services.GetRequiredService<ApprovalSweeper>();
        await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));
        Assert.True(await fixture.Db.ApprovalNudged(approvalId));

        // One nudge per approval: a second sweep adds nothing.
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));
    }

    [Fact]
    public async Task NudgeRepliesInTheBoundConversation()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = "mm-s3",
            ["Providers:Mattermost:Tenant"] = "chat.localhost",
        });
        var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig("consent-policy", """{"nudgeBeforeMinutes":10,"expiresAfterMinutes":60}""");
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "nudge conversation");
        await fixture.Db.SeedBinding("mattermost", "town-square", "post-" + Guid.NewGuid().ToString("N")[..8],
            workId, "consent-s3:" + Guid.NewGuid().ToString("N"));
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromMinutes(5)),
            HttpStatusCode.Accepted);

        await factory.Services.GetRequiredService<ApprovalSweeper>()
            .SweepOnceAsync(CancellationToken.None);

        // Attention to the conversation the work lives in — never zero readers.
        var notice = Assert.Single(await fixture.Db.OutboxEnvelopes(workId), e => e.Contains("lapses soon"));
        Assert.Contains("org.facilitator.command.communication.reply.v1", notice);
        Assert.Contains("town-square", notice);
    }

    [Fact]
    public async Task ExpiryFlipsApprovalAndHeldActionAndParksTheWork()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-s3-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "expiry parks");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1),
                command: HeldCommand, requestedBy: "workload:gw.test"),
            HttpStatusCode.Accepted);
        await fixture.Db.SetApprovalExpires(approvalId, DateTime.UtcNow.AddMinutes(-1));

        var expired = await factory.Services.GetRequiredService<ApprovalSweeper>()
            .SweepOnceAsync(CancellationToken.None);
        Assert.True(expired >= 1);

        Assert.Equal("expired", (await fixture.Db.Approval(approvalId))!.State);
        Assert.Equal("expired", (await fixture.Db.HeldAction(approvalId))!.State);
        var work = await fixture.Db.WorkStateRow(workId);
        Assert.NotNull(work);
        Assert.Equal("blocked", work.Value.State);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "work.consent-expired"));
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "work.consent-expired"));
        Assert.Contains("consent-expired", detail);
    }

    // ---- chat consent = a ticket (C7) -----------------------------------------

    [Fact]
    public async Task ChatDecisionDispatchesTrackerRecordAndCarrierWork()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = "fj-s3",
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:IssueRequestCapability"] = "vcs.issue.create",
        });
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "chat ticket");
        await fixture.Db.SeedBinding("forgejo", "demo/widgets", "42", workId, "consent-s3:" + Guid.NewGuid().ToString("N"));
        // C9: the decider has a verified account on the tracker.
        await fixture.Db.SeedProviderIdentity(
            "forgejo", "git.localhost", "alice-dev", "https://keycloak/realms/ofac", "alice");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);

        await factory.Services.GetRequiredService<ConsentTicketService>()
            .RecordAsync(approvalId, "approved", "oidc:https://keycloak/realms/ofac|alice", CancellationToken.None);

        // The tracker record rides the provider plane: create + decision comment.
        var create = await fixture.Db.OutboxEnvelopeByCommand($"consent-ticket:{approvalId:N}");
        Assert.NotNull(create);
        using var createDoc = JsonDocument.Parse(create);
        Assert.Equal("vcs.issue.create",
            createDoc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());
        Assert.Equal("demo/widgets",
            createDoc.RootElement.GetProperty("data").GetProperty("inputs").GetProperty("repo").GetString());
        // C9: the ticket names the human owner-account, never the bot alone.
        Assert.Equal("alice-dev",
            createDoc.RootElement.GetProperty("data").GetProperty("inputs").GetProperty("ownerProviderAccountId").GetString());
        Assert.Equal("link.fj-s3", (await fixture.Db.OutboxRecipientsByCommand($"consent-ticket:{approvalId:N}")).Single());

        var comment = await fixture.Db.OutboxEnvelopeByCommand($"consent-ticket:{approvalId:N}:comment");
        Assert.NotNull(comment);
        using var commentDoc = JsonDocument.Parse(comment);
        var commentData = commentDoc.RootElement.GetProperty("data");
        Assert.Equal("vcs.issue.comment", commentData.GetProperty("capabilityId").GetString());
        Assert.Contains("approved", commentData.GetProperty("inputs").GetProperty("body").GetString());
        Assert.Contains("oidc:", commentData.GetProperty("inputs").GetProperty("body").GetString());

        // A separate work item carries the ticket (objective names the approval).
        var carrier = await fixture.Db.WorkIdByObjectiveFragment($"Consent decision ticket {approvalId:N}");
        Assert.NotNull(carrier);
        Assert.NotEqual(workId, carrier!.Value);
    }

    // C9: a tracker exists but the decider has NO verified account on it -
    // an ownerless ticket is the accountability hole, so no ticket rides and
    // the note names why.
    [Fact]
    public async Task TrackerWithoutOwnerAccountRecordsTheGap()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = "fj-s3",
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:IssueRequestCapability"] = "vcs.issue.create",
        });
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "unlinked decider");
        await fixture.Db.SeedBinding(
            "forgejo", "demo/c9-widgets", "c9-" + Guid.NewGuid().ToString("N")[..8], workId,
            "consent-c9:" + Guid.NewGuid().ToString("N"));
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:carol"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);

        // carol has NO linked account on any provider, wherever the suite
        // stands (other tests link alice).
        await factory.Services.GetRequiredService<ConsentTicketService>()
            .RecordAsync(approvalId, "approved", "oidc:https://keycloak/realms/ofac|carol", CancellationToken.None);

        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "approval.decided"));
        using var doc = JsonDocument.Parse(detail);
        Assert.Equal("owner-unlinked", doc.RootElement.GetProperty("tracker").GetString());
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"consent-ticket:{approvalId:N}"));
    }

    [Fact]
    public async Task ChatDecisionWithoutTrackerRecordsIdentityAndNamesTheGap()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "no tracker");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);

        await factory.Services.GetRequiredService<ConsentTicketService>()
            .RecordAsync(approvalId, "denied", "oidc:https://keycloak/realms/ofac|alice", CancellationToken.None);

        // No silent nothing: the decided note carries the person AND names the
        // missing tracker.
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "approval.decided"));
        using var doc = JsonDocument.Parse(detail);
        Assert.Equal("missing", doc.RootElement.GetProperty("tracker").GetString());
        Assert.Equal("denied", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("oidc:https://keycloak/realms/ofac|alice", doc.RootElement.GetProperty("decidedBy").GetString());
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"consent-ticket:{approvalId:N}"));
    }

    // ---- helpers (mirror the sibling approval suites' local builders) ---------

    private static string RequiredEnvelope(
        Guid approvalId, Guid? workId, IReadOnlyList<string> actors, TimeSpan life,
        JsonObject? command = null, string? requestedBy = null, string? ruleId = null, bool notRequester = false)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var expires = (DateTime.UtcNow + life).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.required.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["approvalId"] = approvalId.ToString();
        data["eligibleActors"] = new JsonArray(actors.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        data["expiresAt"] = expires;
        data["createdAt"] = now;
        data["nonce"] = "n-" + Guid.NewGuid().ToString("N");
        data["action"] = JsonNode.Parse(DefaultAction.ToJsonString());
        if (workId is not null)
        {
            data["workId"] = workId.Value.ToString();
        }

        if (command is not null)
        {
            data["command"] = JsonNode.Parse(command.ToJsonString());
        }

        if (requestedBy is not null)
        {
            data["requestedBy"] = requestedBy;
        }

        if (ruleId is not null)
        {
            data["ruleId"] = ruleId;
        }

        if (notRequester)
        {
            data["notRequester"] = true;
        }

        data["actionDigest"] = ApprovalDigest.Compute(
            approvalId.ToString(),
            JsonDocument.Parse(DefaultAction.ToJsonString()).RootElement,
            actors,
            DateTime.Parse(expires, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        return envelope.ToJsonString();
    }

    private static string ConsumeEnvelope(Guid approvalId) =>
        JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = "evt-" + Guid.NewGuid().ToString("N"),
            source = "urn:workspace:gw.test",
            type = "org.facilitator.command.approval.consume.v1",
            subject = $"approval/{approvalId}",
            time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/command.approval.consume.v1.json",
            data = new
            {
                commandId = "cmd-" + Guid.NewGuid().ToString("N"),
                approvalId = approvalId.ToString(),
                actorEvidenceRef = "workload:gw.test",
            },
        });

    private static string RespondEnvelope(Guid approvalId, string decision, string evidenceRef) =>
        JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = "evt-" + Guid.NewGuid().ToString("N"),
            source = "urn:workspace:gw.test",
            type = "org.facilitator.command.approval.respond.v1",
            subject = $"approval/{approvalId}",
            time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/command.approval.respond.v1.json",
            data = new
            {
                commandId = "cmd-" + Guid.NewGuid().ToString("N"),
                approvalId = approvalId.ToString(),
                decision,
                actorEvidenceRef = evidenceRef,
            },
        });

    private static async Task<string> CanonicalActorAsync(string user)
    {
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            token.Split('.')[1].PadRight(token.Split('.')[1].Length + (4 - token.Split('.')[1].Length % 4) % 4, '='))));
        return $"oidc:{KeycloakFixture.Issuer}|{payload.RootElement.GetProperty("sub").GetString()}";
    }

    private static async Task<HttpClient> CreateUserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
