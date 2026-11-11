using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Teams;

/// <summary>
/// The team roster and the scoped switch plane: roster CRUD and its authority
/// split (an operator anywhere, a team-operator for its own team only), a
/// foreign team's private answers as not-found, the built-in role names no
/// config write can remove, config-driven roster bootstrap, and the
/// system → team → channel precedence of one switch.
/// Realm posture: <c>alice</c> is a framework-admin (sees everything),
/// <c>bob</c> is a plain member of team-beta only, <c>carol</c> carries no
/// group claim. The CLI token holds kernel-operator.
/// Joins <c>postgres:persistence</c>: schema-bound rows on the shared DB.
/// </summary>
[Collection("postgres:persistence")]
public sealed class TeamRosterTests(PostgresFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private HttpClient _operator = null!;
    private string _bob = null!;
    private string _alice = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _operator = _factory.CreateAuthedClient();
        _bob = await KeycloakFixture.ActorOfAsync("bob", "bob");
        _alice = await KeycloakFixture.ActorOfAsync("alice", "alice");
        await fixture.Db.PurgeTeamRoster("team-alpha");
        await fixture.Db.PurgeTeamRoster("team-beta");
        await fixture.Db.PurgeTeamSettings("team-alpha");
        await fixture.Db.PurgeTeamSettings("team-beta");
        await fixture.Db.AdminConfigPurge("team-behavior");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _operator.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpClient> UserAsync(string name)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await KeycloakFixture.TokenForUserAsync(name, name));
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, string path, string body, string reason = "seated for the test", string etag = "*")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Json(body) };
        request.Headers.Add("X-Change-Reason", reason);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-Change-Reason", "removed by the test");
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PutMemberAsync(
        HttpClient client, string team, string actor, string body, string reason = "seated for the test") =>
        PutAsync(client, $"/api/teams/{team}/members/{Uri.EscapeDataString(actor)}", body, reason);

    [Fact]
    public async Task OperatorSeesEveryTeamWithItsOwnStanding()
    {
        var teams = await KernelOps.GetJsonAsync(_operator, "/api/teams", HttpStatusCode.OK);
        var slugs = teams.EnumerateArray()
            .Select(t => t.GetProperty("slug").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("team-alpha", slugs);
        Assert.Contains("team-beta", slugs);
    }

    [Fact]
    public async Task OperatorSeatsAndRemovesAMemberWithAuditedProvenance()
    {
        var put = await PutMemberAsync(_operator, "team-beta", _bob, """{"kind":"human","capacity":"member"}""");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(_bob, JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("actor").GetString());

        var row = await fixture.Db.TeamRosterRow("team-beta", _bob);
        Assert.NotNull(row);
        Assert.Equal("member", row!.Capacity);
        Assert.StartsWith("oidc:", row.AddedBy, StringComparison.Ordinal); // the CLI operator is the actor of record

        var audit = await fixture.Db.AuditRowsFor("teams.roster.write");
        Assert.Contains(audit, r => r.Resource == $"teams/team-beta/members/{_bob}" && r.Decision == "allow");

        // A roster row makes the team configured, and the directory names the caller's capacity.
        using var bobClient = await UserAsync("bob");
        var teams = await KernelOps.GetJsonAsync(bobClient, "/api/teams", HttpStatusCode.OK);
        var beta = teams.EnumerateArray()
            .Single(t => t.GetProperty("slug").GetString() == "team-beta");
        Assert.True(beta.GetProperty("rosterConfigured").GetBoolean());
        Assert.Equal("member", beta.GetProperty("callerCapacity").GetString());
        // bob is scoped: the other team is not in his directory at all.
        Assert.DoesNotContain(teams.EnumerateArray(), t => t.GetProperty("slug").GetString() == "team-alpha");

        var remove = await DeleteAsync(_operator, $"/api/teams/team-beta/members/{Uri.EscapeDataString(_bob)}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Null(await fixture.Db.TeamRosterRow("team-beta", _bob));
    }

    [Fact]
    public async Task TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutMemberAsync(
            _operator, "team-beta", _bob, """{"kind":"human","capacity":"operator"}""")).StatusCode);
        await fixture.Db.SeedTeam("team-gamma");

        using var bobClient = await UserAsync("bob");

        // Own team: a team-operator may seat people.
        Assert.Equal(HttpStatusCode.OK, (await PutMemberAsync(
            bobClient, "team-beta", _alice, """{"kind":"human","capacity":"member"}""")).StatusCode);

        // Another team: no standing at all — not-found, never a confirming 403.
        Assert.Equal(HttpStatusCode.NotFound, (await PutMemberAsync(
            bobClient, "team-gamma", _alice, """{"kind":"human","capacity":"member"}""")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bobClient.GetAsync("/api/teams/team-gamma/members")).StatusCode);
    }

    [Fact]
    public async Task PlainMemberMayReadRosterButNotWriteIt()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutMemberAsync(
            _operator, "team-beta", _bob, """{"kind":"human","capacity":"member"}""")).StatusCode);

        using var bobClient = await UserAsync("bob");
        var roster = await KernelOps.GetJsonAsync(bobClient, "/api/teams/team-beta/members", HttpStatusCode.OK);
        Assert.Contains(roster.EnumerateArray(), m => m.GetProperty("actor").GetString() == _bob);

        var denial = await PutMemberAsync(bobClient, "team-beta", _alice, """{"kind":"human","capacity":"member"}""");
        Assert.Equal(HttpStatusCode.Forbidden, denial.StatusCode);
        Assert.Null(await fixture.Db.TeamRosterRow("team-beta", _alice));
    }

    [Fact]
    public async Task AWorkOfAForeignTeamIsInvisibleNotForbidden()
    {
        await fixture.Db.SeedTeam("team-gamma");
        var gammaProject = await fixture.Db.SeedProject("team-gamma");
        var (workId, _) = await KernelOps.CreateWorkAsync(_operator, "team-gamma work", gammaProject);
        var betaProject = await fixture.Db.SeedProject("team-beta");
        var (ownId, _) = await KernelOps.CreateWorkAsync(_operator, "team-beta work", betaProject);

        using var bobClient = await UserAsync("bob");

        // bob is scoped to team-beta: gamma's work answers not-found, his own answers.
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/works/{workId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/works/{ownId}")).StatusCode);

        var list = await KernelOps.GetJsonAsync(bobClient, "/api/works", HttpStatusCode.OK);
        var ids = list.EnumerateArray().Select(w => w.GetProperty("workId").GetGuid()).ToList();
        Assert.Contains(ownId, ids);
        Assert.DoesNotContain(workId, ids);

        // The unscoped operator still sees both.
        Assert.Equal(HttpStatusCode.OK, (await _operator.GetAsync($"/api/works/{workId}")).StatusCode);
    }

    [Fact]
    public async Task RosterSeedsFromConfigThenDbIsTruth()
    {
        var carol = await KeycloakFixture.ActorOfAsync("carol", "carol");
        using var seeded = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["identity:roster:0:team"] = "team-alpha",
            ["identity:roster:0:actor"] = carol,
            ["identity:roster:0:capacity"] = "operator",
        });
        seeded.CreateClient(); // starts the host, which runs the seeder

        var row = await fixture.Db.TeamRosterRow("team-alpha", carol);
        Assert.NotNull(row);
        Assert.Equal("operator", row!.Capacity);
        Assert.Equal("config", row.AddedBy);

        // Config never overwrites: a human change survives the next boot.
        Assert.Equal(HttpStatusCode.OK, (await PutMemberAsync(
            _operator, "team-alpha", carol, """{"kind":"human","capacity":"member"}""", "demoted by hand")).StatusCode);
        Assert.Equal("member", (await fixture.Db.TeamRosterRow("team-alpha", carol))!.Capacity);
    }

    [Fact]
    public async Task BuiltInRoleNamesCannotBeRemovedByConfig()
    {
        var response = await _operator.DeleteAsync("/api/identity/roles/team-operator");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal(
            "https://errors.openfacilitator.org/v1/role-builtin",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task TeamSwitchOverridesTheSystemDefaultAndIsVersioned()
    {
        var first = await PutAsync(_operator, "/api/teams/team-alpha/settings/team-behavior",
            """{"scopeKind":"team","scopeId":"team-alpha","payload":{"quiet":true}}""");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, body.GetProperty("rowVersion").GetInt64());

        var stored = await fixture.Db.TeamSetting("team-alpha", "team", "team-alpha", "team-behavior");
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.RowVersion);
        Assert.True(JsonDocument.Parse(stored.Payload).RootElement.GetProperty("quiet").GetBoolean());
        Assert.StartsWith("oidc:", stored.Author, StringComparison.Ordinal);

        // The fence is real.
        var stale = await PutAsync(_operator, "/api/teams/team-alpha/settings/team-behavior",
            """{"scopeKind":"team","scopeId":"team-alpha","payload":{"quiet":false}}""", etag: "\"7\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(1, (await JsonWire.ReadJsonAsync(stale.Content)).GetProperty("currentVersion").GetInt64());

        // Channel scope beats team scope for that channel.
        var channel = await PutAsync(_operator, "/api/teams/team-alpha/settings/team-behavior",
            """{"scopeKind":"channel","scopeId":"mattermost/general","payload":{"quiet":false,"progressVerbosity":"verbose"}}""");
        Assert.Equal(HttpStatusCode.OK, channel.StatusCode);
        Assert.NotNull(await fixture.Db.TeamSetting("team-alpha", "channel", "mattermost/general", "team-behavior"));

        // The resolver answers channel first, then team, then system — over the
        // wire, and every answer names the level it came from.
        var viaChannel = await KernelOps.GetJsonAsync(_operator,
            "/api/teams/team-alpha/effective/team-behavior?channel=mattermost/general", HttpStatusCode.OK);
        Assert.Equal("channel", viaChannel.GetProperty("level").GetString());
        var viaTeam = await KernelOps.GetJsonAsync(_operator,
            "/api/teams/team-alpha/effective/team-behavior?channel=mattermost/other", HttpStatusCode.OK);
        Assert.Equal("team", viaTeam.GetProperty("level").GetString());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _operator.GetAsync("/api/teams/team-beta/effective/team-behavior")).StatusCode);

        var audit = await fixture.Db.AuditRowsFor("teams.effective.resolve");
        static string? LevelOf(TestDbProbe.AuditRow row) =>
            JsonDocument.Parse(row.Detail).RootElement.TryGetProperty("level", out var l) ? l.GetString() : null;
        Assert.Contains(audit, r => r.Resource == "teams/team-alpha/team-behavior" && LevelOf(r) == "channel");
        Assert.Contains(audit, r => r.Resource == "teams/team-alpha/team-behavior" && LevelOf(r) == "team");
    }

    [Fact]
    public async Task RosterMutationWithoutAChangeReasonIsRefused()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/teams/team-beta/members/oidc%3Aiss%7Csub")
        {
            Content = Json("""{"kind":"human","capacity":"member"}"""),
        };
        var response = await _operator.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
