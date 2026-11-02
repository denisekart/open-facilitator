using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace Facilitator.Tests.Link;

/// <summary>
/// Mode-(b) enrollment + oidc: evidence against the shared Keycloak
/// realm (spec §6): CC enrollment round-trip with rotation, the pre-registration
/// law (workload-not-registered, role-mismatch), and the evidence five-step
/// outcomes (delegation-validated extends the principal; tampered/expired/
/// wrong-audience/missing-delegation deny 422 + audit). Mode-(a) negatives
/// (team-unknown, gw. prefix) live in <c>EnrollmentTests</c>.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class WorkloadIdentityTests(DispatchDbFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task ModeBRoundTripRotatesAndOldTokenDies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-gateway-alpha", "gateway-alpha-secret");

        var first = await EnrollModeB(kernel, "gw.team-alpha", cc);
        var second = await EnrollModeB(kernel, "gw.team-alpha", cc);
        Assert.NotEqual(first, second);

        // Rotation kills the old token immediately: 401 unauthorized.
        var stale = factory.CreateLinkClient(first);
        using var probe = await stale.GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        var body = await probe.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body, StringComparison.OrdinalIgnoreCase);

        // The live token IS a principal (404 capabilities-not-found, not 401).
        var fresh = factory.CreateLinkClient(second);
        using var ok = await fresh.GetAsync("/api/link/v1/capabilities");
        Assert.Contains("capabilities-not-found", await ok.Content.ReadAsStringAsync());

        // The binding's team is the principal's team: a manifest naming
        // another roster team is 403 team-mismatch (real-roster re-run).
        var mismatch = JsonNode.Parse(LinkTestHarness.Manifest("team-beta"))!.AsObject();
        var wrong = await fresh.PostAsync("/api/link/v1/capabilities",
            new StringContent(mismatch.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("team-mismatch", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnregisteredWorkloadSubjectIs403()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        // ofac-worker-alpha IS registered — but for gw.team-beta only: the
        // (clientId, endpointId) pair lookup must fail closed.
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-worker-alpha", "worker-alpha-secret");
        using var response = await EnrollRaw(kernel, "gw.somewhere-else", cc);
        await AssertProblem(response, HttpStatusCode.Forbidden, "workload-not-registered");
    }

    [Fact]
    public async Task BodyRoleContradictingBindingIs403()
    {
        // Extra pre-registration: a CHILD-role binding (index 2 beyond the dev
        // pair) so a body role:gateway contradicts it.
        var settings = new Dictionary<string, string?>
        {
            ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
            ["Link:WorkloadIdentities:2:EndpointId"] = "ws.child-binding",
            ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
            ["Link:WorkloadIdentities:2:Role"] = "child",
            ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "false",
        };
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-gateway-alpha", "gateway-alpha-secret");
        using var response = await EnrollRaw(kernel, "ws.child-binding", cc, role: "gateway");
        await AssertProblem(response, HttpStatusCode.Forbidden, "role-mismatch");
    }

    [Fact]
    public async Task ValidOidcEvidenceExtendsPrincipalOnAcceptingEnrollment()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-gateway-alpha", "gateway-alpha-secret");
        var link = factory.CreateLinkClient(await EnrollModeB(kernel, "gw.team-alpha", cc));
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "delegation evidence");

        var evidence = await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");
        var aliceSub = SubjectOf(evidence);

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            SteerWithEvidence(workId, version, $"oidc:{evidence}"), HttpStatusCode.Accepted);

        var row = Assert.Single(await fixture.Db.AuditRows("link:gw.team-alpha", "allow"));
        Assert.Equal("authorization.evaluated:link", row.Action);
        Assert.Equal("delegation-validated", row.Reason);
        Assert.Contains("delegatedSubject", row.Detail, StringComparison.Ordinal);
        Assert.Contains(aliceSub, row.Detail, StringComparison.Ordinal);
        Assert.Contains(KeycloakFixture.Issuer, row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegationOnNonAcceptingBindingIsRefusedButAllowed()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-worker-alpha", "worker-alpha-secret");
        var link = factory.CreateLinkClient(await EnrollModeB(kernel, "gw.team-beta", cc));
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "refused delegation");

        var evidence = await KeycloakFixture.TokenForUserAsync("bob", "bob", "ofac-evidence", "evidence-secret");
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            SteerWithEvidence(workId, version, $"oidc:{evidence}"), HttpStatusCode.Accepted);

        var row = Assert.Single(await fixture.Db.AuditRows("link:gw.team-beta", "allow"));
        Assert.Equal("delegation-refused", row.Reason);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("wrong-audience")]
    [InlineData("missing-delegation")]
    public async Task FailedOidcEvidenceDeniesWithAudit(string mode)
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-gateway-alpha", "gateway-alpha-secret");
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        // Failed-evidence denial is enrollment-mode independent: a mode-(a)
        // binding keeps this test off the (clientId, endpointId) binding matrix.
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "failed evidence");

        var (evidence, expected) = mode switch
        {
            // Signature: flip the last payload-char of the signature segment.
            "tamper" => (Tamper(await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret")), "signature"),
            // facilitator-cli mints aud facilitator-api only.
            "wrong-audience" => (KeycloakFixture.CliToken, "wrong-audience"),
            // Valid aud/sig, but a service-account token carries no delegation flag.
            _ => (cc, "missing-delegation"),
        };

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(SteerWithEvidence(workId, version, $"oidc:{evidence}"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("evidence-invalid", body);
        Assert.Contains(expected, body);

        var row = Assert.Single(await fixture.Db.AuditRows($"link:{endpoint}", "deny"));
        Assert.Equal($"evidence-invalid:{expected}", row.Reason);
    }

    [Fact]
    public async Task ExpiredOidcEvidenceDenies()
    {
        // ofac-evidence mints 1s tokens; the factory pins the validator's
        // clock skew to 0 so expiry is deterministic (+4s sleep margin).
        var settings = new Dictionary<string, string?> { ["Link:Evidence:ClockSkewSeconds"] = "0" };
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        var kernel = factory.CreateAuthedClient();
        var cc = await KeycloakFixture.TokenForClientAsync("ofac-gateway-alpha", "gateway-alpha-secret");
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "expired evidence");

        var evidence = await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");
        await Task.Delay(4000);

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(SteerWithEvidence(workId, version, $"oidc:{evidence}"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("expired", await response.Content.ReadAsStringAsync());

        var row = Assert.Single(await fixture.Db.AuditRows($"link:{endpoint}", "deny"));
        Assert.Equal("evidence-invalid:expired", row.Reason);
    }

    private static async Task<string> EnrollModeB(HttpClient kernel, string endpointId, string ccJwt, string? role = null)
    {
        using var response = await EnrollRaw(kernel, endpointId, ccJwt, role);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("linkToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> EnrollRaw(HttpClient kernel, string endpointId, string ccJwt, string? role = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["token"] = ccJwt,
            ["endpointId"] = endpointId,
            ["instanceId"] = "inst-b",
            ["trustMode"] = "shared-runtime",
        };
        if (role is not null)
        {
            body["role"] = role;
        }

        return await kernel.PostAsync("/api/link/v1/enroll",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string problemSuffix)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ProblemBase + problemSuffix, body, StringComparison.Ordinal);
    }

    private static string SubjectOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
        return doc.RootElement.GetProperty("sub").GetString()!;
    }

    private static string Tamper(string jwt)
    {
        var parts = jwt.Split('.');
        var sig = parts[2];
        parts[2] = sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A');
        return string.Join('.', parts);
    }

    private static string SteerWithEvidence(Guid workId, long version, string evidenceRef)
    {
        var root = JsonNode.Parse(PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", workId, version))!.AsObject();
        root["data"]!.AsObject()["actorEvidenceRef"] = evidenceRef;
        return root.ToJsonString();
    }
}
