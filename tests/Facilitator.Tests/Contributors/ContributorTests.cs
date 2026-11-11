using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Contributors;

/// <summary>
/// Non-human contributors as first-class team members (K-family): the named
/// team-owned directory, roster integration, assignability, the retirement law
/// (open work moves to the team, history keeps the name), remit changes as
/// audited operator actions, the enroll-time contributor binding, and the
/// contributor-only-capability refusal for human evidence (N13/P9).
/// Joins <c>postgres:persistence</c>: schema-bound rows on the shared DB.
/// </summary>
[Collection("postgres:persistence")]
public sealed class ContributorTests(PostgresFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private HttpClient _operator = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _operator = _factory.CreateAuthedClient();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _operator.Dispose();
        _factory.Dispose();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? body, string? reason)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        if (reason is not null)
        {
            request.Headers.Add("X-Change-Reason", reason);
        }

        return await client.SendAsync(request);
    }

    private async Task<(string Team, Guid ProjectId)> FreshTeamAsync()
    {
        var slug = $"ct-w-{Guid.NewGuid():N}"[..24];
        await fixture.Db.SeedTeam(slug);
        return (slug, await fixture.Db.SeedProject(slug));
    }

    private async Task<JsonElement> PutContributorAsync(
        string team, string handle, object remit, string reason = "registered for the test")
    {
        var response = await SendAsync(
            _operator, HttpMethod.Put, $"/api/teams/{team}/contributors/{handle}",
            JsonSerializer.Serialize(new { displayName = handle.ToUpperInvariant(), remit }), reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task RegistrationSeatsTheContributorOnTheRoster()
    {
        var (team, _) = await FreshTeamAsync();
        var view = await PutContributorAsync(team, "ci-bot",
            new { capabilityIds = new[] { "ci.run" }, operations = new[] { "merge" } });
        Assert.Equal("active", view.GetProperty("status").GetString());
        Assert.Equal("ci-bot", view.GetProperty("handle").GetString());

        // One place for "who belongs": the contributor is on the roster as a
        // contributor-kind member, not silently a human.
        var members = await KernelOps.GetJsonAsync(
            _operator, $"/api/teams/{team}/members", HttpStatusCode.OK);
        var row = members.EnumerateArray()
            .Single(m => m.GetProperty("actor").GetString() == "contributor:ci-bot");
        Assert.Equal("contributor", row.GetProperty("kind").GetString());

        // The team-scoped listing answers too.
        var listed = await KernelOps.GetJsonAsync(
            _operator, $"/api/teams/{team}/contributors", HttpStatusCode.OK);
        Assert.Equal(1, listed.GetArrayLength());
    }

    [Fact]
    public async Task HandleCollisionsAcrossTeamsRefuseTheWrite()
    {
        var (teamA, _) = await FreshTeamAsync();
        var (teamB, _) = await FreshTeamAsync();
        await PutContributorAsync(teamA, "shared-bot", new { capabilityIds = Array.Empty<string>(), operations = Array.Empty<string>() });
        var response = await SendAsync(
            _operator, HttpMethod.Put, $"/api/teams/{teamB}/contributors/shared-bot",
            JsonSerializer.Serialize(new { remit = new { capabilityIds = Array.Empty<string>(), operations = Array.Empty<string>() } }),
            "clash for the test");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RemitChangeIsAnAuditedOperatorAction()
    {
        var (team, _) = await FreshTeamAsync();
        await PutContributorAsync(team, "audited-bot",
            new { capabilityIds = new[] { "ci.run" }, operations = Array.Empty<string>() });

        var response = await SendAsync(
            _operator, HttpMethod.Put, "/api/contributors/audited-bot/remit",
            JsonSerializer.Serialize(new { remit = new { capabilityIds = new[] { "ci.run", "ci.gate" }, operations = new[] { "merge" } } }),
            "widened remit after the incident review");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail(await response.Content.ReadAsStringAsync());
        }

        // K7: the trail names author + reason + date.
        using var historyResponse = await _operator.GetAsync("/api/contributors/audited-bot/history");
        if (historyResponse.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail(await historyResponse.Content.ReadAsStringAsync());
        }

        var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync()).RootElement;
        var changes = history.GetProperty("changes");
        var change = changes.EnumerateArray()
            .First(c => c.GetProperty("action").GetString() == "contributors.remit");
        Assert.Equal("widened remit after the incident review", change.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(change.GetProperty("author").GetString()));
        Assert.NotEqual(default, change.GetProperty("at").GetDateTime());
    }

    [Fact]
    public async Task MissingReasonRefusesEveryContributorWrite()
    {
        var (team, _) = await FreshTeamAsync();
        var response = await SendAsync(
            _operator, HttpMethod.Put, $"/api/teams/{team}/contributors/no-reason",
            JsonSerializer.Serialize(new { remit = new { capabilityIds = Array.Empty<string>(), operations = Array.Empty<string>() } }),
            reason: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RetirementMovesOpenWorkToTheTeamAndKeepsTheName()
    {
        var (team, projectId) = await FreshTeamAsync();
        await PutContributorAsync(team, "worker-bot",
            new { capabilityIds = new[] { "ci.run" }, operations = Array.Empty<string>() });
        var openWork = await fixture.Db.SeedWorkOwned(projectId, "contributor:worker-bot", "active");
        var doneWork = await fixture.Db.SeedWorkOwned(projectId, "contributor:worker-bot", "completed");

        var response = await SendAsync(
            _operator, HttpMethod.Post, "/api/contributors/worker-bot/retire",
            "{}", "bot decommissioned");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // K10: the open work loses its owner (the team attention view carries
        // it); the finished work is untouched.
        Assert.Null(await fixture.Db.WorkOwnerActor(openWork));
        Assert.Equal("contributor:worker-bot", await fixture.Db.WorkOwnerActor(doneWork));

        // B3's law: the row endures and still names itself.
        var view = await KernelOps.GetJsonAsync(_operator, "/api/teams/" + team + "/contributors", HttpStatusCode.OK);
        var retired = view.EnumerateArray().Single(c => c.GetProperty("handle").GetString() == "worker-bot");
        Assert.Equal("retired", retired.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, retired.GetProperty("retiredAt").ValueKind);
    }

    [Fact]
    public async Task EnrollCanBindAnEndpointToItsTeamsContributor()
    {
        var (team, _) = await FreshTeamAsync();
        await PutContributorAsync(team, "runner-bot",
            new { capabilityIds = new[] { "ci.run" }, operations = Array.Empty<string>() });

        var body = JsonSerializer.Serialize(new
        {
            token = "enroll-dev",
            endpointId = "wkr-ct-" + Guid.NewGuid().ToString("N")[..8],
            instanceId = "inst-1",
            trustMode = "isolated-executor",
            team,
            contributor = "runner-bot",
        });
        await KernelOps.PostJsonAsync(_operator, "/api/link/v1/enroll", body, HttpStatusCode.Created);
        Assert.Equal("runner-bot", await fixture.Db.EnrollmentContributorHandle(
            JsonSerializer.Deserialize<JsonElement>(body).GetProperty("endpointId").GetString()!));

        // A foreign-team contributor refuses the whole enroll.
        var foreign = JsonSerializer.Serialize(new
        {
            token = "enroll-dev",
            endpointId = "wkr-ct-f" + Guid.NewGuid().ToString("N")[..7],
            instanceId = "inst-1",
            trustMode = "isolated-executor",
            team,
            contributor = "worker-from-elsewhere",
        });
        using var refused = await _operator.PostAsync("/api/link/v1/enroll", new StringContent(foreign, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted()
    {
        var (team, projectId) = await FreshTeamAsync();
        await PutContributorAsync(team, "ci-owner",
            new { capabilityIds = new[] { "ci.contributor-only" }, operations = Array.Empty<string>() });

        // A kernel-minted evidence row whose subject is a human (N13: a human
        // cannot be assigned to a capability only a contributor holds).
        var evidenceId = "ct-human-" + Guid.NewGuid().ToString("N");
        await fixture.Db.SeedEvidence(evidenceId, await KeycloakFixture.ActorOfAsync("alice", "alice"), null, null);

        var workId = await fixture.Db.SeedWorkOwned(projectId, "contributor:ci-owner", "planning");
        var commandId = "ct-intent-" + Guid.NewGuid().ToString("N");
        await KernelOps.PostJsonAsync(_operator, $"/api/works/{workId}/redispatch",
            JsonSerializer.Serialize(new
            {
                capabilityId = "ci.contributor-only",
                commandId,
                actorEvidenceRef = $"kernel:{evidenceId}",
            }), HttpStatusCode.Accepted);
        var intentCommandId = $"routing:work-redispatch:{commandId}";

        var audit = await RoutingHarness.DrainUntilAsync(
            _factory,
            async () =>
            {
                await fixture.Db.ExpireOutboxBackoff(workId);
                var row = await fixture.Db.RoutingDecision(intentCommandId);
                return row is { Status: "abandoned" } ? row : null;
            },
            "the contributor-only refusal lands");
        Assert.Equal("human-against-contributor-only", audit.Basis);

        // Loud, not silent: the triage leg disclosed it once (no candidate arm).
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "work.triaged"));
        Assert.Equal(0, await fixture.Db.OutboxCountBy(workId, "pending"));
    }
}
