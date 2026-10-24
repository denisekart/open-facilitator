using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// §26.1 item 2 (enrollment half) + appendix §4: enrollment mints a per-endpoint
/// link token, wrong bootstrap secret is 401 <c>enrollment-secret-invalid</c>,
/// re-enrollment rotates the token (the old one stops validating immediately),
/// trustMode is carried, and a first team is seeded on demand with slug "default".
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class EnrollmentTests(PostgresFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task EnrollReturnsTokenAndTrustModeAndSeedsDefaultTeam()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var endpointId = NewEndpoint();

        var json = await KernelOps.PostJsonAsync(
            client,
            "/api/link/v1/enroll",
            JsonSerializer.Serialize(new { token = "enroll-dev", endpointId, instanceId = "inst-1", trustMode = "isolated-executor" }),
            System.Net.HttpStatusCode.Created);

        AssertTrue(json.GetProperty("enrollmentId").GetString() is string id && Guid.TryParse(id, out _), "enrollmentId is a guid");
        var trustMode = json.GetProperty("trustMode").GetString();
        Assert.Equal("isolated-executor", trustMode);
        var linkToken = json.GetProperty("linkToken").GetString()!;
        Assert.True(linkToken.Length >= 43, "linkToken is 256-bit base64url");
        AssertTrue(json.GetProperty("expiresAt").TryGetDateTime(out var expiresAt), "expiresAt is a timestamp");
        AssertTrue(expiresAt > DateTime.UtcNow.AddHours(719), "expiresAt honors the 720h default lifetime");

        // enrollment record fields stay server-side (appendix §4): exactly these four
        Assert.Equal(4, json.EnumerateObject().Count());

        // the token IS the principal: capabilities answers 404 capabilities-not-found
        // (enrolled, but never advertised) rather than 401.
        var link = factory.CreateLinkClient(linkToken);
        using var probe = await link.GetAsync("/api/link/v1/capabilities");
        var body = await probe.Content.ReadAsStringAsync();
        Assert.Contains("capabilities-not-found", body);

        // Plan 11 replaces this seeding; the FK binds to the on-demand default team.
        Assert.True(await fixture.Db.TeamExists("default"));
    }

    [Fact]
    public async Task WrongEnrollmentSecretIs401TypedProblem()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new { token = "nope", endpointId = NewEndpoint(), instanceId = "i", trustMode = "shared-runtime" }),
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(ProblemBase + "enrollment-secret-invalid", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ReEnrollRotatesTokenAndInvalidatesTheOldOne()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var endpointId = NewEndpoint();

        var first = await KernelOps.PostJsonAsync(
            client,
            "/api/link/v1/enroll",
            JsonSerializer.Serialize(new { token = "enroll-dev", endpointId, instanceId = "inst-1", trustMode = "isolated-executor" }),
            System.Net.HttpStatusCode.Created);
        var oldToken = first.GetProperty("linkToken").GetString()!;

        var second = await KernelOps.PostJsonAsync(
            client,
            "/api/link/v1/enroll",
            JsonSerializer.Serialize(new { token = "enroll-dev", endpointId, instanceId = "inst-2", trustMode = "isolated-executor" }),
            System.Net.HttpStatusCode.Created);
        var newToken = second.GetProperty("linkToken").GetString()!;
        Assert.NotEqual(oldToken, newToken);

        // §26.1 item 2 lever: the rotated-away token stops validating immediately.
        using var oldProbe = await factory.CreateLinkClient(oldToken).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, oldProbe.StatusCode);

        using var newProbe = await factory.CreateLinkClient(newToken).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, newProbe.StatusCode); // capabilities-not-found, principal accepted
    }

    private static string NewEndpoint() => "gw-test-" + Guid.NewGuid().ToString("N")[..12];

    private static void AssertTrue(bool condition, string what) => Assert.True(condition, what);
}
