using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Identity;

/// <summary>
/// §26.1 item 5 (claim-path/issuer/audience halves) + the plan's negative
/// proof: a realm-minted token holding the mapped role passes the
/// <c>work.write</c> gate; a token without it is 403'd with a deny audit row
/// (reason <c>no-role-match</c>), and the audit <c>detail</c> never stores a
/// token. Runtime mutability runs on a probe binding this collection's
/// factories carry alone (operations index 2), so the shared seeded roles
/// stay intact for the parallel collections.
/// </summary>
[Collection("postgres:persistence")]
public sealed class IdentityGateTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MappedRoleTokenWritesAndAnonymousIs401()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var client = factory.CreateAuthedClient();

        using var created = await client.PostAsync("/api/works",
            new StringContent("""{"objective":"gate happy path"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var anonymous = await factory.CreateClient().GetAsync("/api/works");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task RolelessUserTokenIsForbiddenWithDenyAudit()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var token = await KeycloakFixture.TokenForUserAsync("carol", "carol");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.PostAsync("/api/works",
            new StringContent("""{"objective":"carol cannot write"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal("https://errors.openfacilitator.org/v1/forbidden", problem.GetProperty("type").GetString());

        var denies = (await fixture.Db.AuditRows(decision: "deny"))
            .Where(static r => r.Action == "authorization.evaluated:work.write")
            .ToList();
        Assert.NotEmpty(denies);
        Assert.Equal("no-role-match", denies[0].Reason);
        Assert.StartsWith("oidc:", denies[0].Actor, StringComparison.Ordinal);
        Assert.Matches("rv\\d+", denies[0].PolicyVersion);

        // The audit spine never stores a raw token.
        Assert.All(denies, row => Assert.DoesNotContain(token, row.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeRoleEditFlipsDecisionAndRowVersionGuards()
    {
        var settings = new Dictionary<string, string?>
        {
            // Declared so the config-seeded identity_roles row satisfies the
            // gate's unknown-role check at boot; the test PUTs real rules.
            ["identity:roles:gate-probe:0:all:0:claim"] = "/gate-probe-unseeded",
            ["identity:roles:gate-probe:0:all:0:op"] = "exists",
            ["identity:authorization:operations:2:id"] = "gate.probe",
            ["identity:authorization:operations:2:methods:0"] = "GET",
            ["identity:authorization:operations:2:paths:0"] = "/api/dev/deliveries",
            ["identity:authorization:operations:2:rolesAnyOf:0"] = "gate-probe",
        };
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        var admin = factory.CreateAuthedClient();
        const string grantRules = """[{"all":[{"claim":"/sub","op":"exists"}]}]""";
        const string brokenRules = """[{"all":[{"claim":"/nonexistent","op":"exists"}]}]""";

        // Create the probe role (rowVersion 1) and pass the gate.
        using (var create = await admin.PutAsync("/api/identity/roles/gate-probe",
            new StringContent($$"""{"rules":{{grantRules}}}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var body = await create.Content.ReadJsonAsync();
            Assert.Equal(1, body.GetProperty("rowVersion").GetInt64());
        }

        using (var probe = factory.CreateAuthedClient().GetAsync("/api/dev/deliveries"))
        {
            Assert.Equal(HttpStatusCode.OK, (await probe).StatusCode);
        }

        // Stale rowVersion ⇒ 409, decision unchanged.
        using (var stale = await admin.PutAsync("/api/identity/roles/gate-probe",
            new StringContent($$"""{"rules":{{brokenRules}},"rowVersion":99}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        // Force-write broken rules; a fresh factory = fresh snapshot (DB truth).
        using (var edit = await admin.PutAsync("/api/identity/roles/gate-probe",
            new StringContent($$"""{"rules":{{brokenRules}}}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        }

        using var broken = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        using (var denied = await broken.CreateAuthedClient().GetAsync("/api/dev/deliveries"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        Assert.Contains((await fixture.Db.AuditRows(decision: "deny")),
            static r => r.Action == "authorization.evaluated:gate.probe" && r.Reason == "no-role-match");
        Assert.Contains((await fixture.Db.AuditRows(decision: "allow")),
            static r => r.Action == "authorization.evaluated:gate.probe" && r.Reason == "role-match");
        Assert.Equal(2, (await fixture.Db.AuditRows()).Count(r => r.Action == "identity.role.upsert"
            && r.Detail.Contains("gate-probe", StringComparison.Ordinal)));

        // DELETE: present ⇒ 204 + audit; absent ⇒ 409.
        using (var remove = await admin.DeleteAsync("/api/identity/roles/gate-probe"))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }

        Assert.DoesNotContain("gate-probe", await fixture.Db.IdentityRoleNames());
        Assert.Contains((await fixture.Db.AuditRows()),
            static r => r.Action == "identity.role.remove");
        using (var again = await admin.DeleteAsync("/api/identity/roles/gate-probe"))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
    }

    [Fact]
    public async Task MalformedRuleBodyIsRejected()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = factory.CreateAuthedClient();

        using var bad = await admin.PutAsync("/api/identity/roles/garbage-role",
            new StringContent("""{"rules":[{"all":[{"claim":"not-a-pointer","op":"wat","value":"x"}]}]}""",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadJsonAsync();
        Assert.Equal("https://errors.openfacilitator.org/v1/schema-violation", problem.GetProperty("type").GetString());
        Assert.DoesNotContain("garbage-role", await fixture.Db.IdentityRoleNames());
    }
}
