// The two-team router walk: the marquee proof of the ownership law, end to
// end through public surfaces only. A telegram mention with one inbound
// artifact arrives on the router team; the enabled `router-ingress` gallery
// flow resolves the channel to team-alpha, SIGNS the artifact over (a column
// move, digest re-verified, audited, ledger-stamped) and dispatches the work
// there — the born-team binding rides the plane's work-create row. At review
// a decided approval fires `team-home`: the destination plane acknowledges on
// alpha's own dispatch plane, no router leg. The work completes; the reply
// rents the router bot exactly one grant read of the now-foreign bytes, and
// the delivery receipt closes the rental. The second message on the same
// thread is BORN on team-alpha (ingest resolves the conversation's team from
// the binding): the router leg runs exactly once per thread.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.ScenarioWalks;

[Collection("postgres:walks")]
public sealed class RouterSignOverWalkTests(PostgresFixture fixture) : IAsyncLifetime
{
    // The gallery pair ships disabled; this walk toggles them in the shared
    // walks DB, so teardown puts them back on the shelf whatever happened.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE facilitator.workflow_definitions SET enabled = false"
            + " WHERE def_id IN ('router-ingress', 'team-home')", connection);
        await command.ExecuteNonQueryAsync();
    }

    // The gallery flow's v1 resolver law: alpha-channel belongs to team-alpha.
    private const string AlphaChannel = "alpha-channel";
    private const string RouterTenant = "api.telegram.org";

    private static readonly byte[] Photo = "telegram photo bytes\n"u8.ToArray();

    private readonly string _suffix = WalkKit.Leaf();
    private readonly string _root = "thread-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _router = "walk-rt-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _alpha = "walk-al-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task RouterIngressSignsOverGrantsReplyReadAndBirthsTheThread()
    {
        var router = _router;
        var alpha = _alpha;
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Facilitator:Workflows:Enabled"] = "true",
            ["Link:Teams:2"] = "router",
            ["Providers:Telegram:Enabled"] = "true",
            ["Providers:Telegram:EndpointId"] = router,
            ["Providers:Telegram:Tenant"] = RouterTenant,
            ["Providers:Telegram:BotUserId"] = "777000",
            ["Providers:Telegram:SendManifestArtifacts"] = "true",
        });
        var kernel = factory.CreateAuthedClient(); // boots migrations + the gallery seed
        var routerLink = factory.LinkClient(await WalkKit.EnrollAsync(kernel, router, team: "router"));
        var alphaLink = factory.LinkClient(await WalkKit.EnrollAsync(kernel, alpha, team: "team-alpha"));
        // Alpha advertises the research capability: team-home's dispatch has
        // a home on alpha's own plane.
        await fixture.Db.SeedCapabilityManifest(alpha,
            """
            {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"team-alpha",
             "capabilities":[{"id":"research.default","version":1,"execution":"asynchronous"}]}
            """);
        await fixture.Db.SeedProviderIdentity("telegram", RouterTenant, "acct-1", "subject-1");
        // The router enrollment's reply descriptor carries the outbound image
        // facet: the completion reply attaches the manifest artifact.
        await fixture.Db.SeedCapabilityManifest(router, RouterReplyManifest);
        // The delivery read surface demands a live link_sessions row — and
        // so does the routing funnel: team-home's dispatch routes to a LIVE
        // alpha session advertising research.default.
        var sessions = factory.Services.GetRequiredService<LinkService>();
        await sessions.OpenSessionAsync(router, "walk-session", null, 300, CancellationToken.None);
        await sessions.OpenSessionAsync(alpha, "walk-session", null, 300, CancellationToken.None);

        // The gallery pair ships disabled; the walk enables the router leg.
        await KernelOps.PostJsonAsync(kernel,
            "/api/admin/workflows/definitions/router-ingress/versions/1/toggle",
            """{"enabled": true}""", HttpStatusCode.OK, "walk: the router leg runs");

        // ---- 1. One inbound artifact + a mention: born on the router team.
        var evidence = "identity-evidence:walk-" + _suffix;
        var upload = await KernelOps.PostJsonAsync(routerLink, "/api/link/v1/events",
            UploadEnvelope("cmd-upload-" + _suffix, evidence), HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(upload.GetProperty("data").GetProperty("artifactId").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await routerLink.PutAsync(
            $"/api/link/v1/artifacts/{artifactId}/content", new ByteArrayContent(Photo))).StatusCode);
        Assert.Equal("router", await ArtifactTeamAsync(artifactId));

        var delivery = $"dlv-{_suffix}";
        await KernelOps.PostJsonAsync(routerLink, "/api/link/v1/events",
            MentionEnvelope(delivery, artifactId, "look at this screenshot"), HttpStatusCode.Accepted);

        // ---- 2. The plane runs the router leg: sign-over, dispatch on alpha.
        // (Ingest answered the plane's deterministic work id synchronously.)
        var judgment = await JudgmentAsync(delivery);
        Assert.Contains("(plane: router-ingress)", judgment);
        var workId = Guid.Parse(judgment["work/".Length..judgment.IndexOf(' ', "work/".Length)]);

        await WalkKit.DrainUntilAsync(factory, async () =>
            await ArtifactTeamAsync(artifactId) == "team-alpha" ? "signed" : null,
            "router-ingress signed the artifact over to team-alpha");
        Assert.Equal(workId, await fixture.Db.BindingWorkId("telegram", AlphaChannel, _root));
        Assert.Equal("team-alpha", await BindingTeamAsync());

        // The audit names the flow rule and the moved row; the ledger row
        // keeps its judgment and carries the ownership stamp.
        var audit = await SignOverAuditAsync();
        Assert.Equal("router-ingress@1:alpha", audit.GetProperty("rule").GetString());
        Assert.Equal("team-alpha", audit.GetProperty("toTeam").GetString());
        Assert.Contains(artifactId.ToString("D"), audit.GetProperty("transferred").GetRawText());
        Assert.EndsWith("|owner:team-alpha", await JudgmentAsync(delivery));
        // The new owner reads its own bytes (strict team scoping, no grant
        // needed): the 200 carries the digest as ETag.
        var ownerRead = await alphaLink.GetAsync($"/api/link/v1/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);

        // ---- 3. Review: the destination plane. A decided approval fires
        // team-home, acknowledging on alpha's own dispatch plane — zero
        // router legs involved.
        await KernelOps.TransitionAsync(kernel, workId, "planning", 1);
        await KernelOps.TransitionAsync(kernel, workId, "active", 2);
        await KernelOps.TransitionAsync(kernel, workId, "review", 3);
        await KernelOps.PostJsonAsync(kernel,
            "/api/admin/workflows/definitions/team-home/versions/1/toggle",
            """{"enabled": true}""", HttpStatusCode.OK, "walk: the home flow runs");
        var approvalId = Guid.NewGuid();
        await fixture.Db.SeedApproval(approvalId, workId);
        var decided = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.approval.decided.json"))!.AsObject();
        decided["id"] = "evt-" + Guid.NewGuid().ToString("N");
        decided["data"]!["approvalId"] = approvalId.ToString();
        decided["data"]!["workId"] = workId.ToString();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events", decided.ToJsonString(), HttpStatusCode.Accepted);
        HomeRow home;
        try
        {
            home = await WalkKit.DrainUntilAsync(factory, async () =>
                await HomeAckAsync(workId, approvalId) is { } row ? new HomeRow(row) : null,
                "team-home acknowledged the decision on alpha's plane");
        }
        catch (TimeoutException)
        {
            foreach (var row in await RowsAsync(
                         "SELECT jsonb_build_object('kind', 'instance', 'def', i.def_id, 'status', i.status,"
                         + " 'step', i.current_step,"
                         + " 'log', (SELECT jsonb_agg(jsonb_build_object('s', l.step_id, 'st', l.status, 'out', l.output))"
                         + "            FROM facilitator.workflow_step_log l WHERE l.instance_id = i.id))"
                         + " FROM facilitator.workflow_instances i"
                         + " WHERE i.def_id IN ('router-ingress','team-home')"))
            {
                System.IO.File.AppendAllText("/tmp/walk-diag.txt", "WALK-DIAG: " + row.GetRawText() + "\n");
            }

            foreach (var row in await RowsAsync(
                         "SELECT jsonb_build_object('kind', 'outbox', 'command', command_id, 'type', event_type,"
                         + " 'recipient', recipient_endpoint_id, 'status', status)"
                         + " FROM facilitator.outbox WHERE aggregate_id = $1::uuid", workId.ToString("D")))
            {
                System.IO.File.AppendAllText("/tmp/walk-diag.txt", "WALK-DIAG: " + row.GetRawText() + "\n");
            }

            foreach (var row in await RowsAsync(
                         "SELECT jsonb_build_object('kind', 'inbox', 'state', state, 'event', payload->>'type')"
                         + " FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.approval.decided.v1'"
                         + " ORDER BY received_at DESC LIMIT 3"))
            {
                System.IO.File.AppendAllText("/tmp/walk-diag.txt", "WALK-DIAG: " + row.GetRawText() + "\n");
            }

            throw;
        }
        Assert.Equal($"link.{alpha}", home.Row.GetProperty("recipient").GetString());
        Assert.Contains("decided: approved", home.Row.GetProperty("text").GetString());

        // ---- 4. Completion: the reply rents one grant read of foreign bytes.
        await fixture.Db.SeedResultManifest(
            $"manifest-{workId:N}", workId, artifactId, [artifactId.ToString("N")], "recorded", "validated");
        await KernelOps.TransitionAsync(kernel, workId, "completed", 4, reason: "walk: signed-over work done");
        var reply = await ReplyGrantAsync(workId, artifactId);
        Assert.NotNull(reply);
        Assert.Equal("active", reply.Value.GetProperty("status").GetString());
        Assert.Equal("router", reply.Value.GetProperty("granteeTeam").GetString());

        // The router principal reads the ALPHA-owned bytes exactly once.
        var first = await routerLink.GetAsync($"/api/link/v1/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(Photo.LongLength, (await first.Content.ReadAsByteArrayAsync()).LongLength);
        Assert.Equal(HttpStatusCode.NotFound,
            (await routerLink.GetAsync($"/api/link/v1/artifacts/{artifactId}/content")).StatusCode);
        Assert.Equal(new[] { "artifact.grant.consume", "artifact.grant.read" },
            (await GrantActionsAsync(artifactId)).OrderBy(a => a, StringComparer.Ordinal).ToArray());

        // The delivery receipt lands (the rental's other ending): the grant
        // was consumed by the read, so the receipt adds nothing.
        var claimed = await KernelOps.GetJsonAsync(routerLink, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
        var replyDelivery = claimed.EnumerateArray().Single(d =>
            Carries(d, reply.Value.GetProperty("commandId").GetString()!));
        using var receipt = await routerLink.PostAsync(
            $"/api/link/v1/deliveries/{replyDelivery.GetProperty("deliveryId").GetString()}/receipt",
            new StringContent("""{"status":"accepted","processedAt":"2026-11-17T00:00:00Z"}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, receipt.StatusCode);
        var afterReceipt = await ReplyGrantAsync(workId, artifactId);
        Assert.Equal("consumed", afterReceipt!.Value.GetProperty("status").GetString());
        Assert.Equal("read", afterReceipt.Value.GetProperty("consumeReason").GetString());

        // ---- 5. The second message is BORN on team-alpha: no new router leg.
        var signOversBefore = await SignOverCountAsync();
        var bornDelivery = $"dlv-born-{_suffix}";
        await KernelOps.PostJsonAsync(routerLink, "/api/link/v1/events",
            MentionEnvelope(bornDelivery, artifactId, "and a follow-up question"), HttpStatusCode.Accepted);
        // The born message routed as plain work input — NOT as a plane run
        // (no `(plane: …)` in the judgment). The terminal work forks its
        // successor under the arm's own follow-up law, so the judgment names
        // a work without asserting which one.
        Assert.EndsWith(":input", await JudgmentAsync(bornDelivery));
        Assert.DoesNotContain("plane:", await JudgmentAsync(bornDelivery));
        // The work the born message routed to (the bound work or its forked
        // successor) is ALPHA's own: the conversation's team rode the ingest.
        var bornJudgment = await JudgmentAsync(bornDelivery);
        var bornWorkId = Guid.Parse(bornJudgment["work/".Length..bornJudgment.IndexOf(":input", StringComparison.Ordinal)]);
        Assert.Equal("team-alpha", await WorkTeamAsync(bornWorkId));
        Assert.Equal(signOversBefore, await SignOverCountAsync());
        Assert.Equal("team-alpha", await ArtifactTeamAsync(artifactId));
    }

    // ------------------------------------------------------------------ kit

    private sealed record HomeRow(JsonElement Row);

    private static bool Carries(JsonElement delivery, string commandId) =>
        delivery.GetProperty("envelope").GetProperty("data").TryGetProperty("idempotencyKey", out var key)
        && key.GetString() == commandId;

    // The reply descriptor's media facet: images go out.
    private static string RouterReplyManifest =>
        """
        {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"router",
         "capabilities":[{"id":"org.facilitator.command.communication.reply.v1","version":1,
                          "execution":"synchronous",
                          "media":{"directions":["in","out"],"kinds":["image/*"],
                                   "maxCount":8,"maxBytesPerItem":8388608}}]}
        """;

    private static string UploadEnvelope(string commandId, string evidence)
    {
        var envelope = JsonNode
            .Parse(PostgresFixture.ReadFixture("valid/command.artifact.request-upload.json"))!.AsObject();
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["commandId"] = commandId;
        data["mediaType"] = "image/png"; // the outbound image facet filters by kind
        data["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(Photo)).ToLowerInvariant();
        data["size"] = Photo.LongLength;
        data["actorEvidenceRef"] = evidence;
        data.AsObject().Remove("workId");
        data.AsObject().Remove("assignmentId");
        return envelope.ToJsonString();
    }

    private string MentionEnvelope(string delivery, Guid artifactId, string text)
    {
        var doc = JsonNode
            .Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.with-media.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["provider"] = "telegram";
        data["providerDeliveryId"] = delivery;
        data["actorProviderAccountId"] = "acct-1";
        data["text"] = text;
        var reference = data["reference"]!.AsObject();
        reference["provider"] = "telegram";
        reference["tenantOrOrganizationId"] = RouterTenant;
        reference["channelOrResourceId"] = AlphaChannel;
        reference["rootThreadId"] = _root;
        reference["messageId"] = delivery;
        reference["authorProviderAccountId"] = "acct-1";
        data["sourceRefs"]!.AsArray()[0]!.AsObject()["artifactId"] = artifactId.ToString();
        return doc.ToJsonString();
    }

    private async Task<List<JsonElement>> RowsAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var rows = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = reader.GetFieldValue<string>(0);
            // jsonb columns arrive as JSON; plain columns get wrapped.
            rows.Add(JsonDocument.Parse(
                value.Length > 0 && value[0] is '{' or '[' or '"'
                    ? value
                    : JsonSerializer.Serialize(value)).RootElement.Clone());
        }

        return rows;
    }

    private async Task<string?> ArtifactTeamAsync(Guid artifactId) =>
        await RowsAsync("SELECT coalesce(team_slug,'none') FROM facilitator.artifacts WHERE artifact_id = $1",
            artifactId) is [{ } row] ? row.GetString() : null;

    private async Task<string?> WorkTeamAsync(Guid workId) =>
        await RowsAsync(
            "SELECT coalesce(t.slug,'none') FROM facilitator.works w"
            + " LEFT JOIN facilitator.teams t ON t.team_id = w.team_id WHERE w.work_id = $1", workId)
            is [{ } row] ? row.GetString() : null;

    private async Task<string?> BindingTeamAsync() =>
        await RowsAsync(
            "SELECT coalesce(team,'none') FROM facilitator.communication_bindings"
            + " WHERE provider = 'telegram' AND channel = $1 AND root_message_id = $2", AlphaChannel, _root)
            is [{ } row] ? row.GetString() : null;

    private async Task<string> JudgmentAsync(string delivery) =>
        (await RowsAsync(
            "SELECT coalesce(verified_reference,'') FROM facilitator.provider_events"
            + " WHERE provider = 'telegram' AND provider_delivery_id = $1", delivery))[0].GetString()!;

    private async Task<int> SignOverCountAsync() =>
        int.Parse((await RowsAsync(
            "SELECT count(*)::int::text FROM facilitator.audit WHERE action = 'artifact.sign-over'"))[0]
            .GetString()!);

    private async Task<JsonElement> SignOverAuditAsync() =>
        Assert.Single(await RowsAsync(
            "SELECT detail::jsonb FROM facilitator.audit"
            + " WHERE action = 'artifact.sign-over' AND decision = 'allow'"));

    private async Task<JsonElement?> ReplyGrantAsync(Guid workId, Guid artifactId) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('commandId', o.command_id, 'status', g.status,
                                      'granteeTeam', g.grantee_team_slug, 'consumeReason', g.consume_reason)
            FROM facilitator.outbox o
              JOIN facilitator.artifact_grants g ON g.command_id = o.command_id
            WHERE o.aggregate_id = $1::uuid
              AND o.event_type = 'org.facilitator.command.communication.reply.v1'
              AND g.artifact_id = $2::uuid
            """, workId.ToString("D"), artifactId.ToString("D")) is [{ } row] ? row : null;

    private async Task<List<string>> GrantActionsAsync(Guid artifactId) =>
        (await RowsAsync(
            "SELECT action FROM facilitator.audit WHERE action LIKE 'artifact.grant.%' AND resource = $1",
            $"artifact/{artifactId}"))
        .Select(r => r.GetString()!).ToList();

    private async Task<JsonElement?> HomeAckAsync(Guid workId, Guid approvalId) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('recipient', recipient_endpoint_id,
                                      'text', envelope::jsonb -> 'data' -> 'inputs' ->> 'text')
            FROM facilitator.outbox WHERE aggregate_id = $1::uuid
              AND recipient_endpoint_id = $3
              AND envelope::jsonb -> 'data' -> 'inputs' ->> 'text' LIKE $2
            """, workId.ToString("D"), $"%{approvalId} decided: approved%", $"link.{_alpha}")
            is [{ } row] ? row : null;
}
