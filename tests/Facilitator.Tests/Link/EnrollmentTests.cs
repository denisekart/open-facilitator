using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// Enrollment law: a bootstrap CODE buys an identity, never a bearer — the
/// response is the key's fingerprint plus the replay fence. Wrong code is 401
/// <c>enrollment-secret-invalid</c>; re-enroll with a new key retires the old
/// one, whose proof stops validating immediately; rotation is two-phase (both
/// keys sign the same payload) and the retired key keeps proving only inside
/// <c>Link:RotateGraceMinutes</c>; a session is 5 minutes of cached authority
/// that a revoke or a fence move can end early; and the enrollment binds to the
/// config-seeded team roster.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class EnrollmentTests(DispatchDbFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task EnrollReturnsKeyIdAndTrustModeAndSeedsDefaultTeam()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var endpointId = NewEndpoint();

        var enrolled = await LinkTestHarness.EnrollAsync(client, endpointId);

        AssertTrue(enrolled.Enrollment.GetProperty("enrollmentId").GetString() is string id && Guid.TryParse(id, out _),
            "enrollmentId is a guid");
        Assert.Equal("isolated-executor", enrolled.Enrollment.GetProperty("trustMode").GetString());
        Assert.Matches(@"^sha256:[0-9a-f]{64}$", enrolled.KeyId);
        Assert.Equal(0, enrolled.Enrollment.GetProperty("seq").GetInt64());

        // No secret crosses the wire in either direction: exactly these four fields.
        Assert.Equal(4, enrolled.Enrollment.EnumerateObject().Count());

        // The key IS the principal: capabilities answers 404 capabilities-not-found
        // (enrolled, but never advertised) rather than 401.
        var link = factory.CreateLinkClient(enrolled);
        using var probe = await link.GetAsync("/api/link/v1/capabilities");
        var body = await probe.Content.ReadAsStringAsync();
        Assert.Contains("capabilities-not-found", body);

        // The FK binds to the roster-seeded team (Link:Teams config, no on-demand default).
        Assert.True(await fixture.Db.TeamExists("team-alpha"));
    }

    [Fact]
    public async Task WrongEnrollmentSecretIs401TypedProblem()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "nope",
                    endpointId = NewEndpoint(),
                    instanceId = "i",
                    trustMode = "shared-runtime",
                    publicKey = LinkTestHarness.NewEd25519().PublicKey,
                    keyAlgorithm = "ed25519",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(ProblemBase + "enrollment-secret-invalid", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AKeylessEnrollmentIsASchemaViolation()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        // publicKey became required precisely so a client cannot silently fall back
        // to the retired bearer: a code-only enrollment is a shape error, not a
        // degraded success. The team guard runs first (the roster is public), so the
        // body names a roster team to reach the key check.
        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "enroll-dev",
                    endpointId = NewEndpoint(),
                    instanceId = "i",
                    trustMode = "shared-runtime",
                    team = "team-alpha",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("publicKey", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReEnrollWithANewKeyRetiresTheOldOneImmediately()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var endpointId = NewEndpoint();

        var first = await LinkTestHarness.EnrollAsync(client, endpointId);
        var second = await LinkTestHarness.EnrollAsync(client, endpointId);
        Assert.NotEqual(first.KeyId, second.KeyId);

        // Lever: the retired key cannot even get a session — its proof is refused.
        using var retired = await first.ProveRawAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, retired.StatusCode);
        Assert.Contains("proof-invalid", await retired.Content.ReadAsStringAsync());

        using var newProbe = await factory.CreateLinkClient(second).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, newProbe.StatusCode); // capabilities-not-found, principal accepted
    }

    [Fact]
    public async Task ProofByTheWrongKeyIs401()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());

        // A foreign hand signing a proof that CLAIMS the enrolled fingerprint.
        var (nonce, epoch, seq) = await enrolled.ChallengeAsync();
        var (foreignSeed, _) = LinkTestHarness.NewEd25519();
        using var response = await enrolled.ProveWithAsync(
            enrolled.ProofWith(nonce, epoch, seq, seed: foreignSeed));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("proof-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProofReplayIs401()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());

        var jti = "jti-replay-" + Guid.NewGuid().ToString("N");
        var (nonce, epoch, seq) = await enrolled.ChallengeAsync();
        using var first = await enrolled.ProveWithAsync(
            enrolled.ProofWith(nonce, epoch, seq, jti: jti));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A fresh nonce does not buy a second life for a used jti: the fence is
        // monotonic, and one proof may move it exactly once.
        var (nonce2, epoch2, seq2) = await enrolled.ChallengeAsync();
        using var replay = await enrolled.ProveWithAsync(
            enrolled.ProofWith(nonce2, epoch2, seq2, jti: jti));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("proof-invalid", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AProofUnderTheFenceIsAReplaySuspect()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());

        // Two live challenges, and a rotation that moves the fence between them:
        // the older nonce is still fresh, but the state it was minted against is
        // now below the fence, so the kernel treats it as a replay attempt.
        var (staleNonce, epoch, staleSeq) = await enrolled.ChallengeAsync();
        var rotated = await enrolled.RotateAsync(LinkTestHarness.NewEd25519(), seq: staleSeq + 7);
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);

        using var response = await enrolled.ProveWithAsync(
            enrolled.ProofWith(staleNonce, epoch, staleSeq));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("replay-suspect", body);

        var row = Assert.Single(await fixture.Db.AuditRowsFor("link.replay-suspect"));
        Assert.Equal("deny", row.Decision);
        Assert.Contains("seq-below-fence", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationNeedsBothKeysAndRetiresTheOldOne()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());
        var (oldSeed, oldKeyId) = (enrolled.Seed, enrolled.KeyId);

        var rotated = await enrolled.RotateAsync(LinkTestHarness.NewEd25519());
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        Assert.NotEqual(oldKeyId, enrolled.KeyId);

        // The new key is the principal again, immediately.
        using var fresh = await enrolled.ProveRawAsync();
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // And the retired key is refused (inside the grace window it may still
        // ROTATE, but it can no longer hold a session).
        var (nonce, epoch, seq) = await enrolled.ChallengeAsync();
        using var stale = await enrolled.ProveWithAsync(
            enrolled.ProofWith(nonce, epoch, seq, seed: oldSeed, sub: oldKeyId));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Contains("proof-invalid", await stale.Content.ReadAsStringAsync());

        Assert.Single(await fixture.Db.AuditRowsFor("link.key-rotated"));
    }

    [Fact]
    public async Task AHalfRotationIsASchemaViolation()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());

        var (nonce, _, fence) = await enrolled.ChallengeAsync();
        var next = LinkTestHarness.NewEd25519();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/link/v1/enroll/{enrolled.EndpointId}/rotate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    oldKeyId = enrolled.KeyId,
                    newPublicKey = next.PublicKey,
                    proofs = new[]
                    {
                        // Only the old key signed: a unilateral takeover attempt.
                        LinkTestHarness.LinkEndpointCredential.RotationProof(enrolled.Seed, enrolled.KeyId, enrolled.KeyId, nonce, fence),
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await enrolled.SessionTokenAsync());

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("two proofs", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RotationUnderTheGraceWindowReusesARetiredKeyAndAuditsIt()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint());
        var (firstSeed, firstKeyId) = (enrolled.Seed, enrolled.KeyId);

        Assert.Equal(HttpStatusCode.NoContent, (await enrolled.RotateAsync(LinkTestHarness.NewEd25519())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await enrolled.RotateAsync(LinkTestHarness.NewEd25519())).StatusCode);

        // Two rotations in, the FIRST key is retired but weeks inside the grace
        // window — the recovery path for a node that lost the swap mid-flight.
        var recovered = await enrolled.RotateAsync(
            LinkTestHarness.NewEd25519(), retiredSeed: firstSeed, retiredKeyId: firstKeyId);
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        Assert.Single(await fixture.Db.AuditRowsFor("link.rotate-grace-consumed"));
    }

    [Fact]
    public async Task RotationOutsideTheGraceWindowIs401()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, fakeClock: true, settings: new Dictionary<string, string?> { ["Link:LeaseSweeperEnabled"] = "false" });
        var client = factory.CreateAuthedClient();
        var enrolled = await LinkTestHarness.EnrollAsync(client, NewEndpoint(), clock: factory.Clock);
        var (firstSeed, firstKeyId) = (enrolled.Seed, enrolled.KeyId);

        Assert.Equal(HttpStatusCode.NoContent, (await enrolled.RotateAsync(LinkTestHarness.NewEd25519())).StatusCode);

        // Grace is a clock, not a courtesy: past Link:RotateGraceMinutes the
        // retired key is only good for a revoke-and-rejoin.
        factory.AdvanceClock(TimeSpan.FromMinutes(61));
        using var expired = await enrolled.RotateAsync(LinkTestHarness.NewEd25519(), retiredSeed: firstSeed, retiredKeyId: firstKeyId);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Contains("key-rotation-grace-expired", await expired.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnExpiredSessionReProvesRatherThanReEnrolls()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, fakeClock: true, settings: new Dictionary<string, string?> { ["Link:LeaseSweeperEnabled"] = "false" });
        var client = factory.CreateAuthedClient();
        var endpointId = NewEndpoint();
        var enrolled = await LinkTestHarness.EnrollAsync(client, endpointId, clock: factory.Clock);

        using (var probe = await factory.CreateLinkClient(enrolled).GetAsync("/api/link/v1/capabilities"))
        {
            Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
        }

        var sessionsBefore = (await fixture.Db.AuditRowsFor("link.session-issued"))
            .Count(r => r.Detail!.Contains(endpointId, StringComparison.Ordinal));
        Assert.Equal(1, sessionsBefore);

        // The session is 300 seconds of cached authority. Past it, the endpoint
        // comes back with a proof — NOT a re-enroll, which would re-open the
        // bootstrap code's authority over a long-lived identity.
        factory.AdvanceClock(TimeSpan.FromSeconds(301));
        using var after = await factory.CreateLinkClient(enrolled).GetAsync("/api/link/v1/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        var sessionsAfter = (await fixture.Db.AuditRowsFor("link.session-issued"))
            .Count(r => r.Detail!.Contains(endpointId, StringComparison.Ordinal));
        Assert.Equal(2, sessionsAfter);

        // Nothing about the bootstrap code is involved any more: the only
        // enrollment audit for this endpoint is the one enroll wrote at the start.
        Assert.Equal(1, (await fixture.Db.AuditRowsFor("link.enrollment-issued"))
            .Count(r => r.Detail!.Contains(endpointId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ModeATeamOutsideRosterIs403TeamUnknown()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "enroll-dev",
                    endpointId = NewEndpoint(),
                    instanceId = "i",
                    trustMode = "shared-runtime",
                    team = "team-not-in-roster",
                    publicKey = LinkTestHarness.NewEd25519().PublicKey,
                    keyAlgorithm = "ed25519",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("team-unknown", body);
    }

    [Fact]
    public async Task ModeAWithoutTeamIs403TeamUnknown()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "enroll-dev",
                    endpointId = NewEndpoint(),
                    instanceId = "i",
                    trustMode = "shared-runtime",
                    publicKey = LinkTestHarness.NewEd25519().PublicKey,
                    keyAlgorithm = "ed25519",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("team-unknown", body);
    }

    [Fact]
    public async Task ChildRoleOnGwDotChannelIs403RoleMismatch()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "enroll-dev",
                    endpointId = "gw.child-claim",
                    instanceId = "i",
                    trustMode = "isolated-executor",
                    team = "team-alpha",
                    role = "child",
                    publicKey = LinkTestHarness.NewEd25519().PublicKey,
                    keyAlgorithm = "ed25519",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("role-mismatch", body);
    }

    [Fact]
    public async Task RotationMayNotReRoleAnEndpoint()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateAuthedClient();
        var endpointId = "child-" + Guid.NewGuid().ToString("N")[..12];

        await KernelOps.PostJsonAsync(client, "/api/link/v1/enroll",
            JsonSerializer.Serialize(new
            {
                token = "enroll-dev",
                endpointId,
                instanceId = "i",
                trustMode = "isolated-executor",
                team = "team-alpha",
                role = "child",
                publicKey = LinkTestHarness.NewEd25519().PublicKey,
                keyAlgorithm = "ed25519",
            }),
            HttpStatusCode.Created);

        using var response = await client.PostAsync(
            "/api/link/v1/enroll",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "enroll-dev",
                    endpointId,
                    instanceId = "i",
                    trustMode = "isolated-executor",
                    team = "team-alpha",
                    role = "gateway",
                    publicKey = LinkTestHarness.NewEd25519().PublicKey,
                    keyAlgorithm = "ed25519",
                }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("role-mismatch", body);
    }

    [Fact]
    public async Task AnUnknownEndpointChallengesBlind()
    {
        using var factory = new KernelTestFactory(fixture);
        var client = factory.CreateClient();

        // No existence oracle: the challenge for an endpoint that has never
        // enrolled answers the same 401 a wrong proof gets.
        using var response = await client.PostAsync(
            "/api/link/v1/challenge",
            new StringContent(
                JsonSerializer.Serialize(new { endpointId = "gw-never-" + Guid.NewGuid().ToString("N")[..8] }),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("unauthorized", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NewEndpoint() => "gw-test-" + Guid.NewGuid().ToString("N")[..12];

    private static void AssertTrue(bool condition, string what) => Assert.True(condition, what);
}
