using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Artifacts;


using Npgsql;

namespace Facilitator.Tests.Workflows;

/// <summary>
/// The <c>sign-over</c> palette step end to end: a mention of a telegram
/// conversation bound to a router-team work runs the ownership leg — the
/// inbound artifact flips to the guard-chosen team and the audit names the
/// flow rule — and the validator law (<c>content-not-signed-over</c>) is
/// provable for flows that dispatch to a foreign team without a preceding
/// sign-over.
/// </summary>
[Collection("postgres:work")]
public sealed class SignOverStepTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DefId = "t.sign-over";

    private static readonly byte[] Bytes = "inbound telegram photo bytes\n"u8.ToArray();

    private KernelTestFactory _factory = null!;
    private HttpClient _kernel = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: false, settings: new Dictionary<string, string?>
        {
            ["Facilitator:Workflows:Enabled"] = "true",
            ["Link:Teams:2"] = "router",
        });
        _kernel = _factory.CreateAuthedClient();
        await PurgeAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _kernel.Dispose();
        _factory.Dispose();
    }

    private async Task PurgeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cascade = new NpgsqlCommand(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id = :d);
            DELETE FROM facilitator.workflow_instances WHERE def_id = :d;
            DELETE FROM facilitator.workflow_definitions WHERE def_id = :d;
            """, connection)
        { Parameters = { new NpgsqlParameter("d", DefId) } };
        await cascade.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SignOverLegMovesOwnershipAuditsFlowRule()
    {
        // A router-team work anchors the conversation; one stored artifact is
        // born on the router team (inbound media), uploaded through the link
        // plane so the digest re-verify the transfer runs can pass.
        var workId = Guid.NewGuid();
        var channel = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var evidence = "oidc:" + await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");
        var router = _factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(_kernel, $"so-step-{suffix}", team: "router"));
        var ack = await KernelOps.PostJsonAsync(router, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope("cmd-step-" + suffix, Bytes, evidence), HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        var put = await router.PutAsync($"/api/link/v1/artifacts/{artifactId}/content", new ByteArrayContent(Bytes));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO facilitator.works (work_id, objective, state, version, owner_actor, team_id, created_at, updated_at)
                VALUES (:w, 'router thread', 'open', 1, 'kernel',
                        (SELECT team_id FROM facilitator.teams WHERE slug = 'router'), now(), now());
                INSERT INTO facilitator.communication_bindings
                    (binding_id, provider, channel, root_message_id, work_id, created_at)
                VALUES (gen_random_uuid(), 'telegram', :c, 'thread-1', :w, now());
                """, connection)
            { Parameters = { new NpgsqlParameter("w", workId), new NpgsqlParameter("c", channel) } };
            await seed.ExecuteNonQueryAsync();
        }

        var def = $$"""
        {
          "id": "{{DefId}}", "version": 1, "title": "router ingress", "description": "sign-over proof",
          "on": { "event": "org.facilitator.event.provider.mention.v1",
                  "where": { "data.reference.provider": "telegram" } },
          "steps": [
            { "id": "own", "title": "hand the media to alpha",
              "sign-over": { "team": "team-alpha", "refs": ["$event.data.sourceRefs"] } },
            { "id": "done", "title": "finish", "on": "own:completed",
              "emit": { "capability": "research.default", "inputs": { "text": "signed over" } } }
          ]
        }
        """;
        await StoreAsync(def);
        await EnableAsync();

        // The telegram mention of the with-media fixture, rewritten for this
        // conversation, carrying the uploaded artifact as its sourceRefs item.
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.with-media.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["reference"]!["channelOrResourceId"] = channel;
        node["data"]!["reference"]!["rootThreadId"] = "thread-1";
        node["data"]!["providerDeliveryId"] = "dlv-" + Guid.NewGuid().ToString("N");
        node["data"]!["sourceRefs"]!.AsArray()[0]!.AsObject()["artifactId"] = artifactId.ToString();
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", node.ToJsonString(), HttpStatusCode.Accepted);

        await KernelOps.PollUntilAsync(async () =>
        {
            var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT team_slug FROM facilitator.artifacts WHERE artifact_id = :a", connection)
            { Parameters = { new NpgsqlParameter("a", artifactId) } };
            return (await cmd.ExecuteScalarAsync() as string) == "team-alpha";
        }, "sign-over leg flips the artifact to team-alpha", 30_000);

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            string actor;
            string detail;
            await using (var audit = new NpgsqlCommand(
                """
                SELECT actor, detail FROM facilitator.audit
                WHERE action = 'artifact.sign-over' AND decision = 'allow'
                ORDER BY occurred_at DESC LIMIT 1
                """, connection))
            await using (var reader = await audit.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                actor = reader.GetString(0);
                detail = reader.GetString(1);
            }

            Assert.Equal("workflow:kernel", actor);
            Assert.Contains("team-alpha", detail, StringComparison.Ordinal);
            Assert.Contains(DefId + "@1:own", detail, StringComparison.Ordinal);

            await using var legs = new NpgsqlCommand(
                """
                SELECT l.status FROM facilitator.workflow_step_log l
                JOIN facilitator.workflow_instances i ON i.id = l.instance_id
                WHERE i.def_id = :d AND l.step_id = 'own'
                """, connection)
            { Parameters = { new NpgsqlParameter("d", DefId) } };
            Assert.Equal("done", await legs.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task DispatchToForeignTeamWithoutPriorSignOverFailsValidation()
    {
        var body = """
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "team": "router",
          "on": { "event": "org.facilitator.event.provider.mention.v1" },
          "steps": [
            { "id": "assign", "title": "x",
              "work-create": { "objectiveRef": "$event.data.text", "capability": "research.default", "team": "team-alpha" } }
          ]
        }
        """;
        var rejected = await KernelOps.PostJsonAsync(_kernel, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(rejected.GetProperty("ok").GetBoolean());
        var error = Assert.Single(rejected.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "content-not-signed-over");
        Assert.Contains("dispatches to team 'team-alpha' with no preceding sign-over",
            error.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The ordering law is index order: the same flow with the sign-over
        // step FIRST validates clean.
        var ordered = """
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "team": "router",
          "on": { "event": "org.facilitator.event.provider.mention.v1" },
          "steps": [
            { "id": "own", "title": "own first", "sign-over": { "team": "team-alpha", "refs": ["$event.data.text"] } },
            { "id": "assign", "title": "x", "on": "own:completed",
              "work-create": { "objectiveRef": "$event.data.text", "capability": "research.default", "team": "team-alpha" } }
          ]
        }
        """;
        var accepted = await KernelOps.PostJsonAsync(_kernel, "/api/admin/workflows/definitions/validate", ordered, HttpStatusCode.OK, "dry run");
        Assert.True(accepted.GetProperty("ok").GetBoolean(),
            accepted.GetProperty("errors").ToString());
    }

    private async Task StoreAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{DefId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "sign-over step test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task EnableAsync() =>
        await KernelOps.PostJsonAsync(_kernel,
            $"/api/admin/workflows/definitions/{DefId}/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.OK, "sign-over step test");
}
