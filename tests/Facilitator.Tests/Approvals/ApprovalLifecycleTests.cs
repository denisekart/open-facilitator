using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Approvals;

/// <summary>
/// The approval lifecycle vertical: record via
/// <c>/api/ingress/events</c> (digest recompute + compare, digest-as-declared
/// for 1.0 senders), the human REST pair (eligibility law D4, single use,
/// lazy expiry, revocation-before-apply, <c>kernel:</c> receipt mint law D5),
/// and the Link-surface respond under a delegated identity. Public surface
/// first; DB assertions through the typed probe.
/// </summary>
[Collection("postgres:work")]
public sealed class ApprovalLifecycleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RequiredWithActionRecordsPendingRowWithKernelDigest()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "digest record");
        var approvalId = Guid.NewGuid();

        var envelope = RequiredEnvelope(approvalId, workId, DefaultAction, DefaultActors, TimeSpan.FromHours(1));
        var accepted = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            envelope, HttpStatusCode.Accepted);
        Assert.Equal("recorded", accepted.GetProperty("status").GetString());

        var row = await fixture.Db.Approval(approvalId);
        Assert.NotNull(row);
        Assert.Equal("pending", row.State);
        Assert.Equal("recomputed", row.DigestVerification);
        Assert.Equal(workId.ToString(), row.WorkId);
        // The stored digest is the kernel's own recompute of this very action:
        // recomputing from the envelope's own timestamps reproduces it.
        Assert.Equal(ExpectedDigest(approvalId, DefaultAction, DefaultActors, Timestamps(envelope).Expires, Timestamps(envelope).Created), row.ActionDigest);
    }

    [Fact]
    public async Task TamperedActionDeniesWithTypedProblemAndFailedInboxRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "digest mismatch");
        var approvalId = Guid.NewGuid();
        var envelope = JsonNode.Parse(RequiredEnvelope(approvalId, workId, DefaultAction, DefaultActors, TimeSpan.FromHours(1)))!;

        // The declared digest stays; the action payload changes → mismatch.
        envelope["data"]!["action"]!["env"] = "prod-us";
        using var response = await kernel.PostAsync("/api/ingress/events",
            new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-digest-mismatch", problem.GetProperty("type").GetString());
        Assert.True(await fixture.Db.InboxFailedFor(envelope!["id"]!.GetValue<string>()));
        Assert.Null(await fixture.Db.Approval(approvalId));
    }

    [Fact]
    public async Task ActionlessSenderRecordsDigestAsDeclaredWithUnverifiedAudit()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var approvalId = Guid.NewGuid();

        // 1.0 senders: the frozen shape (no action) — recorded, never silently verified.
        var envelope = JsonNode.Parse(RequiredEnvelope(approvalId, null, DefaultAction, DefaultActors, TimeSpan.FromHours(1)))!;
        envelope["data"]!.AsObject().Remove("action");
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events", envelope.ToJsonString(), HttpStatusCode.Accepted);

        var row = await fixture.Db.Approval(approvalId);
        Assert.NotNull(row);
        Assert.Equal("digest-unverified", row.DigestVerification);
        var audit = Assert.Single(await fixture.Db.AuditRows(decision: "allow"),
            r => r.Action == "authorization.evaluated:approval" && r.Reason == "digest-unverified");
        Assert.Contains(approvalId.ToString(), audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeliveryUnderFreshEnvelopeIdIsIdempotentDuplicate()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "record idempotence");
        var approvalId = Guid.NewGuid();
        var first = RequiredEnvelope(approvalId, workId, DefaultAction, DefaultActors, TimeSpan.FromHours(1));

        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events", first, HttpStatusCode.Accepted);
        var again = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, DefaultActors, TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);

        Assert.Equal("duplicate", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RestApproveMintsConsumedEvidenceAuditsAndTimelines()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "rest approve");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, ["group:/teams/team-alpha"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        // The list surface shows the pending row to an eligible team member.
        var listed = await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/approvals", HttpStatusCode.OK);
        var listedRow = Assert.Single(listed.EnumerateArray());
        Assert.Equal("pending", listedRow.GetProperty("state").GetString());

        var decided = await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved","comment":"go"}""", HttpStatusCode.OK);
        Assert.Equal("decided", decided.GetProperty("status").GetString());
        Assert.Equal("approved", decided.GetProperty("state").GetString());
        var evidenceRef = decided.GetProperty("evidenceRef").GetString()!;
        Assert.StartsWith("kernel:", evidenceRef, StringComparison.Ordinal);
        var evidenceId = evidenceRef["kernel:".Length..];

        // D5 receipt semantics: the row exists, already consumed.
        Assert.True(await fixture.Db.EvidenceConsumed(evidenceId));
        var row = await fixture.Db.Approval(approvalId);
        Assert.NotNull(row);
        Assert.Equal("approved", row.State);
        Assert.StartsWith("oidc:", row.DecidedBy, StringComparison.Ordinal);

        var allow = Assert.Single(await fixture.Db.AuditRows(decision: "allow"),
            r => r.Action == "authorization.evaluated:approval" && r.Reason == "approved"
                && r.Detail.Contains(approvalId.ToString(), StringComparison.Ordinal));
        Assert.Equal(evidenceRef, allow.EvidenceRef);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.decided"));
    }

    [Fact]
    public async Task SecondDistinctRespondDeniesWithAlreadyConsumed()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "single use");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approvalId}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        using var replay = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"denied"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(replay.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-already-consumed", problem.GetProperty("type").GetString());
        Assert.Contains(await fixture.Db.AuditRows(decision: "deny"),
            r => r.Action == "authorization.evaluated:approval" && r.Reason == "already-consumed");
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.decided"));
    }

    [Fact]
    public async Task ParallelRespondsProduceExactlyOneDecision()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "respond race");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(async attempt =>
        {
            using var response = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
                new StringContent($$"""{"decision":"approved","comment":"race {{attempt}}"}""", Encoding.UTF8, "application/json"));
            return response.StatusCode;
        }));

        Assert.Equal(HttpStatusCode.OK, Assert.Single(races, s => s == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.Conflict, Assert.Single(races, s => s == HttpStatusCode.Conflict));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.decided"));
    }

    [Fact]
    public async Task RespondAfterExpiryFlipsRowLazilyAndDenies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "lazy expiry");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, ["user:alice"], TimeSpan.FromMinutes(5)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        factory.Clock!.Advance(TimeSpan.FromMinutes(10));

        using var response = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-expired", problem.GetProperty("type").GetString());
        Assert.Equal("expired", (await fixture.Db.Approval(approvalId))!.State);
    }

    [Fact]
    public async Task RevokeBeforeRespondDeniesAndRevokeAfterDecideIsAuditedNoOp()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "revocation");
        var revoked = Guid.NewGuid();
        var decided = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(revoked, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(decided, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        // revoke-then-respond ⇒ 409 approval-revoked (consumers must see it).
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RevokedEnvelope(revoked, "superseded"), HttpStatusCode.Accepted);
        using var denied = await alice.PostAsync($"/api/works/{workId}/approvals/{revoked}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-revoked",
            (await JsonWire.ReadJsonAsync(denied.Content)).GetProperty("type").GetString());
        Assert.Equal("revoked", (await fixture.Db.Approval(revoked))!.State);

        // respond-then-revoke ⇒ the revoke is an audited no-op, state holds.
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{decided}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RevokedEnvelope(decided, "too late"), HttpStatusCode.Accepted);
        Assert.Equal("approved", (await fixture.Db.Approval(decided))!.State);
    }

    [Fact]
    public async Task IneligibleActorDeniesWithAuditAndGroupVariantsMatch()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "eligibility");
        var nameMiss = Guid.NewGuid();
        var groupBare = Guid.NewGuid();
        var groupExact = Guid.NewGuid();
        var oidcExact = Guid.NewGuid();
        foreach (var (id, actors) in new[]
        {
            (nameMiss, new[] { "user:bob" }),
            (groupBare, new[] { "group:team-alpha" }),
            (groupExact, new[] { "group:/teams/team-alpha" }),
            (oidcExact, new[] { await AliceCanonicalActorAsync() }),
        })
        {
            await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
                RequiredEnvelope(id, workId, DefaultAction, actors, TimeSpan.FromHours(1)),
                HttpStatusCode.Accepted);
        }

        var alice = await CreateUserClientAsync(factory, "alice");
        using var ineligible = await alice.PostAsync($"/api/works/{workId}/approvals/{nameMiss}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, ineligible.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-actor-ineligible",
            (await JsonWire.ReadJsonAsync(ineligible.Content)).GetProperty("type").GetString());
        Assert.Contains(await fixture.Db.AuditRows(decision: "deny"),
            r => r.Reason == "actor-ineligible" && r.Detail.Contains(nameMiss.ToString(), StringComparison.Ordinal));

        // Both group spellings (raw path and /teams/-stripped) and the exact
        // oidc: form decide fine for alice (team-alpha member).
        foreach (var id in new[] { groupBare, groupExact, oidcExact })
        {
            await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{id}/respond",
                """{"decision":"approved"}""", HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task WorkScopedSurfaceHidesForeignWorkApprovals()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "scope leak");
        var (otherWorkId, _) = await KernelOps.CreateWorkAsync(kernel, "scope leak other");
        var approvalId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approvalId, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        using var cross = await alice.PostAsync($"/api/works/{otherWorkId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"approved"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        var empty = await KernelOps.GetJsonAsync(alice, $"/api/works/{otherWorkId}/approvals", HttpStatusCode.OK);
        Assert.Equal(0, empty.GetArrayLength());

        using var badBody = await alice.PostAsync($"/api/works/{workId}/approvals/{approvalId}/respond",
            new StringContent("""{"decision":"approved","extra":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
    }

    [Fact]
    public async Task WireSurfaceRespondRidesVerifiedEvidenceAndDeniesWorkloadOnly()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "wire respond");
        var ok = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(ok, workId, DefaultAction, ["group:/teams/team-alpha"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(unknown, workId, DefaultAction, ["group:/teams/team-alpha"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var evidence = await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");

        var decided = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RespondEnvelope(ok, $"oidc:{evidence}"), HttpStatusCode.OK);
        Assert.Equal("decided", decided.GetProperty("status").GetString());

        using var workloadOnly = await kernel.PostAsync("/api/ingress/events",
            new StringContent(RespondEnvelope(unknown, "identity-evidence:opaque"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, workloadOnly.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-actor-unknown",
            (await JsonWire.ReadJsonAsync(workloadOnly.Content)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConsumeOnceEnforcesApprovedStateAndRevocationBeforeApply()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "consume hook");
        var approved = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(approved, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RequiredEnvelope(revoked, workId, DefaultAction, ["user:alice"], TimeSpan.FromHours(1)), HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/approvals/{approved}/respond",
            """{"decision":"approved"}""", HttpStatusCode.OK);

        var ledger = CreateLedger(factory);
        var first = await ledger.ConsumeAsync(approved.ToString(), "link:gw.consume", null, CancellationToken.None);
        Assert.Equal(Facilitator.Kernel.Shared.Persistence.ApprovalDecisionOutcome.Decided, first.Outcome);
        var second = await ledger.ConsumeAsync(approved.ToString(), "link:gw.consume", null, CancellationToken.None);
        Assert.Equal(Facilitator.Kernel.Shared.Persistence.ApprovalDecisionOutcome.AlreadyConsumed, second.Outcome);
        Assert.Equal("consumed", (await fixture.Db.Approval(approved))!.State);

        // Revoked-before-consume: the consumer denial names the revocation.
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            RevokedEnvelope(revoked, "superseded"), HttpStatusCode.Accepted);
        var revokedConsume = await ledger.ConsumeAsync(revoked.ToString(), "link:gw.consume", null, CancellationToken.None);
        Assert.Equal(Facilitator.Kernel.Shared.Persistence.ApprovalDecisionOutcome.Revoked, revokedConsume.Outcome);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly JsonNode DefaultAction = JsonNode.Parse(
        """{"operation":"deploy","env":"prod-eu","resources":["repo:open-facilitator"]}""")!;

    private static readonly string[] DefaultActors = ["user:alice"];

    /// <summary>A fresh <c>approval.required</c> envelope whose declared digest is the kernel-computed one.</summary>
    private static string RequiredEnvelope(
        Guid approvalId, Guid? workId, JsonNode action, IReadOnlyList<string> actors, TimeSpan life)
    {
        var now = DateTime.UtcNow.RoundAsWire();
        var expires = (DateTime.UtcNow + life).RoundAsWire();
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.required.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["approvalId"] = approvalId.ToString();
        data["eligibleActors"] = new JsonArray(actors.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        data["expiresAt"] = expires;
        data["createdAt"] = now;
        data["nonce"] = "n-" + Guid.NewGuid().ToString("N");
        data["action"] = JsonNode.Parse(action.ToJsonString());
        if (workId is not null)
        {
            data["workId"] = workId.Value.ToString();
        }

        data["actionDigest"] = ExpectedDigest(approvalId, action, actors, expires, now);
        return envelope.ToJsonString();
    }

    private static string ExpectedDigest(
        Guid approvalId, JsonNode action, IReadOnlyList<string> actors,
        string? expires = null, string? created = null)
    {
        var now = DateTime.UtcNow.RoundAsWire();
        return ApprovalDigest.Compute(
            approvalId.ToString(),
            JsonDocument.Parse(action.ToJsonString()).RootElement,
            actors,
            DateTime.Parse(expires ?? (DateTime.UtcNow + TimeSpan.FromHours(1)).RoundAsWire(), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(created ?? now, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    private static (string Expires, string Created) Timestamps(string envelope)
    {
        var data = JsonNode.Parse(envelope)!["data"]!.AsObject();
        return (data["expiresAt"]!.GetValue<string>(), data["createdAt"]!.GetValue<string>());
    }

    private static string RevokedEnvelope(Guid approvalId, string reason)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.revoked.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["data"]!["approvalId"] = approvalId.ToString();
        envelope["data"]!["reason"] = reason;
        return envelope.ToJsonString();
    }

    private static string RespondEnvelope(Guid approvalId, string actorEvidenceRef)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/command.approval.respond.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["commandId"] = "cmd-" + Guid.NewGuid().ToString("N");
        data["approvalId"] = approvalId.ToString();
        data["actorEvidenceRef"] = actorEvidenceRef;
        return envelope.ToJsonString();
    }

    private static async Task<HttpClient> CreateUserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<string> AliceCanonicalActorAsync()
    {
        var token = await KeycloakFixture.TokenForUserAsync("alice", "alice");
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            token.Split('.')[1].PadRight(token.Split('.')[1].Length + (4 - token.Split('.')[1].Length % 4) % 4, '=')))).RootElement;
        return $"oidc:{KeycloakFixture.Issuer}|{payload.GetProperty("sub").GetString()}";
    }

    private Facilitator.Kernel.Shared.Persistence.IApprovalLedger CreateLedger(KernelTestFactory factory) =>
        factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<Facilitator.Kernel.Shared.Persistence.IApprovalLedger>();
}

file static class WireTime
{
    /// <summary>RFC 3339 second precision — the digest law's canonical rendering.</summary>
    internal static string RoundAsWire(this DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
