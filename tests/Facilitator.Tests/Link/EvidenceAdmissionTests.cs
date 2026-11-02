using System.Text;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Link;

/// <summary>
/// Evidence law on link admission: a kernel-checkable
/// ref that FAILS validation denies admission (422 evidence-invalid + audit
/// deny), any other scheme records a workload-only allow
/// (<c>authorization.evaluated:link</c>), and a Valid ref on an
/// accepts:false enrollment is <c>delegation-refused</c> — workload-only,
/// allowed. oidc: happy paths run against Keycloak in the realm-fixture suite.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class EvidenceAdmissionTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task UnknownKernelEvidenceDeniesWithAuditRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "evidence deny");

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(SteerWithEvidence(workId, version, $"kernel:{Guid.NewGuid():N}"), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("evidence-invalid", body);
        Assert.Contains("unknown-id", body);

        var rows = await fixture.Db.AuditRows($"link:{endpoint}", "deny");
        var row = Assert.Single(rows);
        Assert.Equal("authorization.evaluated:link", row.Action);
        Assert.Equal("evidence-invalid:unknown-id", row.Reason);
        Assert.Equal("identity-v1", row.PolicyVersion);
        Assert.Contains("team-alpha", row.Detail);
    }
    [Fact]
    public async Task ExpiredKernelEvidenceDenies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "evidence expired");
        var id = Guid.NewGuid().ToString("N");
        await fixture.Db.SeedEvidence(id, "oidc:issuer|subject", DateTime.UtcNow.AddMinutes(-1), null);

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(SteerWithEvidence(workId, version, $"kernel:{id}"), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var rows = await fixture.Db.AuditRows($"link:{endpoint}", "deny");
        Assert.Equal("evidence-invalid:expired", Assert.Single(rows).Reason);
    }

    [Fact]
    public async Task ConsumedKernelEvidenceDeniesAsReplay()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "evidence replay");
        var id = Guid.NewGuid().ToString("N");
        await fixture.Db.SeedEvidence(id, "oidc:issuer|subject", null, DateTime.UtcNow);

        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(SteerWithEvidence(workId, version, $"kernel:{id}"), Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var rows = await fixture.Db.AuditRows($"link:{endpoint}", "deny");
        Assert.Equal("evidence-invalid:consumed", Assert.Single(rows).Reason);
    }

    [Fact]
    public async Task ValidKernelEvidenceOnNonAcceptingEnrollmentIsDelegationRefused()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "delegation refused");
        var id = Guid.NewGuid().ToString("N");
        await fixture.Db.SeedEvidence(id, "oidc:issuer|subject", null, null);

        var accepted = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            SteerWithEvidence(workId, version, $"kernel:{id}"),
            System.Net.HttpStatusCode.Accepted);
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());

        // Mode (a) enrollments never accept delegation (security-first law).
        var rows = await fixture.Db.AuditRows($"link:{endpoint}", "allow");
        var row = Assert.Single(rows);
        Assert.Equal("delegation-refused", row.Reason);
        Assert.Contains("\"scheme\": \"kernel\"", row.Detail);
    }

    [Fact]
    public async Task UnverifiedSchemeEvidenceAdmitsWorkloadOnly()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-ev-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "unverified scheme");

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            SteerWithEvidence(workId, version, "identity-evidence:e2e"),
            System.Net.HttpStatusCode.Accepted);

        var rows = await fixture.Db.AuditRows($"link:{endpoint}", "allow");
        var row = Assert.Single(rows);
        Assert.Equal("evidence-unverified:identity-evidence", row.Reason);
    }

    private static string SteerWithEvidence(Guid workId, long version, string evidenceRef)
    {
        var root = JsonNode.Parse(PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", workId, version))!.AsObject();
        root["data"]!.AsObject()["actorEvidenceRef"] = evidenceRef;
        return root.ToJsonString();
    }
}
