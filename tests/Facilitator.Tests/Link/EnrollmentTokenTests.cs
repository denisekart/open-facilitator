using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Npgsql;

namespace Facilitator.Tests.Link;

/// <summary>
/// Mintable enrollment tokens: mint (literal returned once, hash stored),
/// first-use consumption binds the row's team/role and fences the endpoint
/// (a foreign endpoint on a consumed token is 401), re-enroll on the
/// consuming endpoint rotates the pinned key, expiry and revoke answer 401,
/// revoke kills the issued link enrollment, the list never leaks the
/// literal, DELETE is idempotent, and the <c>enrollment-tokens.manage</c>
/// gate refuses a kernel-operator-only principal. Coexists with the
/// static-secret law (EnrollmentTests, unmodified).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class EnrollmentTokenTests(DispatchDbFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task MintThenEnrollBindsAKeyAndAnswersAsPrincipal()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var endpointId = "gw-" + Guid.NewGuid().ToString("N")[..12];

        var minted = await KernelOps.PostJsonAsync(admin, "/api/admin/enroll-tokens",
            JsonSerializer.Serialize(new { label = "node1", team = "team-alpha", role = "gateway" }),
            HttpStatusCode.Created);
        var token = minted.GetProperty("token").GetString()!;
        Assert.StartsWith("et_", token, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(minted.GetProperty("id").GetString(), out _), "id is a guid");
        Assert.True(minted.GetProperty("expiresAt").TryGetDateTime(out var expiresAt), "expiresAt is a timestamp");
        Assert.True(expiresAt > DateTime.UtcNow.AddMinutes(55), "default ttl is 60 minutes");

        // A minted code buys an identity, never a bearer: the response is the
        // key's fingerprint plus the replay fence, and nothing else.
        var enrolled = await LinkTestHarness.EnrollWithTokenAsync(factory.CreateClient(), token, endpointId, role: "gateway");
        Assert.Matches(@"^sha256:[0-9a-f]{64}$", enrolled.KeyId);
        Assert.Equal(4, enrolled.Enrollment.EnumerateObject().Count());

        // The key IS the principal: capabilities 404 capabilities-not-found, not 401.
        using var probe = await factory.CreateLinkClient(enrolled).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
        Assert.Contains("capabilities-not-found", await probe.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConsumedTokenOnAnotherEndpointIs401()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var minted = await MintAsync(admin, suffix, "team-alpha", "gateway");
        var token = minted.GetProperty("token").GetString()!;
        await LinkTestHarness.EnrollWithTokenAsync(factory.CreateClient(), token, $"gw-{suffix}", role: "gateway");

        using var second = await factory.CreateClient().PostAsync("/api/link/v1/enroll", Json(
            new { token, endpointId = $"gw-other-{suffix}", instanceId = "i", trustMode = "isolated-executor", team = "team-alpha", role = "gateway", publicKey = LinkTestHarness.NewEd25519().PublicKey, keyAlgorithm = "ed25519" }));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains("enrollment-secret-invalid", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReEnrollOnConsumingEndpointRotatesThePinnedKey()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var endpointId = "gw-" + Guid.NewGuid().ToString("N")[..12];

        var token = (await MintAsync(admin, endpointId, "team-alpha", "gateway")).GetProperty("token").GetString()!;
        // The code is single-use, so the key-rotating re-enroll replays the
        // CONSUMED code: first-use consumption is what authorizes it.
        var first = await LinkTestHarness.EnrollWithTokenAsync(factory.CreateClient(), token, endpointId, role: "gateway");
        var second = await LinkTestHarness.EnrollWithTokenAsync(factory.CreateClient(), token, endpointId, role: "gateway");
        Assert.NotEqual(first.KeyId, second.KeyId);

        // Lever: the retired key stops proving immediately — refused at the proof
        // gate, so no session can be minted for it.
        using var retired = await first.ProveRawAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, retired.StatusCode);
        Assert.Contains("proof-invalid", await retired.Content.ReadAsStringAsync());
        using var newProbe = await factory.CreateLinkClient(second).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, newProbe.StatusCode); // capabilities-not-found, principal accepted
    }

    [Fact]
    public async Task ExpiredUnconsumedTokenIs401()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var minted = await MintAsync(admin, "expiry", "team-alpha", "gateway", ttlMinutes: 1);
        var token = minted.GetProperty("token").GetString()!;

        // Push the row out of its first-use window directly (no clock fiction).
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var expire = dataSource.CreateCommand(
            "UPDATE facilitator.enrollment_tokens SET expires_at = now() - interval '1 minute' WHERE id = $1");
        expire.Parameters.AddWithValue(Guid.Parse(minted.GetProperty("id").GetString()!));
        var affected = await expire.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);

        using var response = await factory.CreateClient().PostAsync("/api/link/v1/enroll", Json(
            new { token, endpointId = "gw-expired-" + Guid.NewGuid().ToString("N")[..8], instanceId = "i", trustMode = "isolated-executor", team = "team-alpha", role = "gateway", publicKey = LinkTestHarness.NewEd25519().PublicKey, keyAlgorithm = "ed25519" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("enrollment-secret-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RevokeKillsTokenAndIssuedKey()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var endpointId = "gw-" + Guid.NewGuid().ToString("N")[..12];

        var minted = await MintAsync(admin, endpointId, "team-alpha", "gateway");
        var token = minted.GetProperty("token").GetString()!;
        var enrolled = await LinkTestHarness.EnrollWithTokenAsync(factory.CreateClient(), token, endpointId, role: "gateway");

        // A live session, minted BEFORE the revoke: the bearer under test is
        // young enough to still pass its own expiry check.
        var liveSession = await enrolled.SessionTokenAsync();

        using (var revoke = await admin.DeleteAsync($"/api/admin/enroll-tokens/{minted.GetProperty("id").GetString()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        // The issued key died with the token (401, not capabilities-not-found), and
        // it dies MID-SESSION: this bearer was minted before the revoke and is
        // nowhere near its 5-minute expiry, so only the enrollment-row check
        // (the epoch law) can refuse it. A fresh proof is impossible too — the
        // challenge itself now answers 401.
        using var probe = await factory.CreateLinkClient(liveSession).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);

        // And the minted token itself no longer enrolls.
        using var reEnroll = await factory.CreateClient().PostAsync("/api/link/v1/enroll", Json(
            new { token, endpointId, instanceId = "i2", trustMode = "isolated-executor", team = "team-alpha", role = "gateway", publicKey = LinkTestHarness.NewEd25519().PublicKey, keyAlgorithm = "ed25519" }));
        Assert.Equal(HttpStatusCode.Unauthorized, reEnroll.StatusCode);
    }

    [Fact]
    public async Task ListNeverLeaksTheRawLiteral()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var minted = await MintAsync(admin, "list-leak", "team-alpha", "gateway");
        var token = minted.GetProperty("token").GetString()!;

        var body = (await KernelOps.GetJsonAsync(admin, "/api/admin/enroll-tokens", HttpStatusCode.OK)).GetRawText();
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        var rows = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        var mine = rows.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == minted.GetProperty("id").GetString());
        Assert.Equal("list-leak", mine.GetProperty("label").GetString());
        Assert.Equal("team-alpha", mine.GetProperty("team").GetString());
        Assert.Equal("gateway", mine.GetProperty("role").GetString());
        Assert.True(mine.TryGetProperty("consumedAt", out _), "row carries consumedAt");
        Assert.True(mine.TryGetProperty("consumedEndpointId", out _), "row carries consumedEndpointId");
        Assert.True(mine.TryGetProperty("revokedAt", out _), "row carries revokedAt");
    }

    [Fact]
    public async Task DeleteIsIdempotentAndUnknownIs404()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        var minted = await MintAsync(admin, "idempotent", "team-alpha", "gateway");
        var id = minted.GetProperty("id").GetString()!;

        using (var first = await admin.DeleteAsync($"/api/admin/enroll-tokens/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using (var second = await admin.DeleteAsync($"/api/admin/enroll-tokens/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        using var unknown = await admin.DeleteAsync($"/api/admin/enroll-tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(ProblemBase + "enrollment-token-not-found", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GateRefusesKernelOperatorOnlyPrincipal()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        // The shared CLI service account holds kernel-operator (+e2e-operator), NOT framework-admin.
        using var cli = factory.CreateAuthedClient();
        using var post = await cli.PostAsync("/api/admin/enroll-tokens", Json(
            new { label = "gate-probe", team = "team-alpha", role = "gateway" }));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task MintForUnrosteredTeamIs403TeamUnknown()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        using var response = await admin.PostAsync("/api/admin/enroll-tokens", Json(
            new { label = "ghost", team = "team-ghost", role = "gateway" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("team-unknown", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MintRejectsBadBodies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var admin = await AdminClientAsync(factory);
        using (var noLabel = await admin.PostAsync("/api/admin/enroll-tokens", Json(new { team = "team-alpha", role = "gateway" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noLabel.StatusCode);
            Assert.Contains("schema-violation", await noLabel.Content.ReadAsStringAsync());
        }

        using var badRole = await admin.PostAsync("/api/admin/enroll-tokens", Json(new { label = "x", team = "team-alpha", role = "overseer" }));
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Contains("schema-violation", await badRole.Content.ReadAsStringAsync());
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<HttpClient> AdminClientAsync(KernelTestFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await KeycloakFixture.TokenForUserAsync("alice", "alice"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static Task<JsonElement> MintAsync(HttpClient admin, string label, string team, string role, int? ttlMinutes = null) =>
        KernelOps.PostJsonAsync(admin, "/api/admin/enroll-tokens",
            JsonSerializer.Serialize(new { label, team, role, ttlMinutes }), HttpStatusCode.Created);

    private static async Task<JsonElement> EnrollAsync(KernelTestFactory factory, string token, string endpointId, string team, string role) =>
        await KernelOps.PostJsonAsync(factory.CreateClient(), "/api/link/v1/enroll",
            JsonSerializer.Serialize(new { token, endpointId, instanceId = "inst-1", trustMode = "isolated-executor", team, role }),
            HttpStatusCode.Created);
}
