using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// The shadow gate (Phase 2): a review on terminal work runs THROUGH the
/// arm (the real fork, real reply, real judgment) while the plane computes
/// the SAME fork in shadow — step-log rows only, would-do records, no
/// effects. The walk seeds the review shapes the swap will face and
/// asserts, field by field, that the plane's plan equals the arm's actual
/// effects: successor identity under the deterministic law, the objective
/// and link rows, the binding repoint, the reply command id + sentence
/// (modulo the successor short id the arm mints randomly). Any mismatch
/// fails here, not in production after the swap.
/// Mirrors <see cref="ReviewForkWalkTests"/> (same forgejo settings; the
/// arm's behavior must stay untouched by the shadow leg).
/// </summary>
[Collection("postgres:walks")]
public sealed class ReviewForkShadowWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ShadowPlanEqualsArmEffects_ForTerminalAndLiveReviews()
    {
        var endpoint = "gw-shadow-" + WalkKit.Leaf();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
        });
        factory.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
        var kernel = factory.CreateAuthedClient();
        var link = factory.LinkClient(await WalkKit.EnrollAsync(kernel, endpoint));
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        await fixture.Db.SeedProviderIdentity("forgejo", "git.localhost", "acct-1", "subject-1");

        // review-fork v1 (gallery-seeded, disabled): enable + shadow.
        await KernelOps.PostJsonAsync(kernel,
            "/api/admin/workflows/definitions/review-fork/versions/1/toggle",
            """{"enabled": true, "shadow": true}""", HttpStatusCode.OK, "phase 2 shadow walk");

        // ---- shape 1: review on TERMINAL work (the fork path) ----
        var repo = $"repo-{WalkKit.Leaf()}";
        var pr = $"pr-{WalkKit.Leaf()}";
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "forgejo", "git.localhost",
                $"dlv-{WalkKit.Leaf()}", repo, pr, "acct-1", "review this branch"),
            HttpStatusCode.Accepted);
        var workId = await fixture.Db.BindingWorkId("forgejo", repo, pr);
        Assert.NotNull(workId);
        await KernelOps.TransitionAsync(kernel, workId.Value, "planning", 1);
        await KernelOps.TransitionAsync(kernel, workId.Value, "cancelled", 2);

        var reviewDelivery = $"rev-{WalkKit.Leaf()}";
        var review = ReviewEnvelope(repo, pr, reviewDelivery, "changes-requested", "guard the null case");
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", review, HttpStatusCode.Accepted);

        // The arm ran for real: successor + successorOf link (link kind,
        // note), the binding followed the live item, one reply row.
        var successor = Assert.Single(await ValsAsync(
            "SELECT ref_work_id::text FROM facilitator.work_links WHERE work_id = $1 AND kind = 'successor'", workId.Value));
        var armSuccessor = Guid.Parse(successor);
        Assert.Equal(1, await fixture.Db.WorkLinkCount(workId.Value, armSuccessor, "successor"));
        var (bound, _) = await fixture.Db.BindingFor("forgejo", repo, pr);
        Assert.Equal(armSuccessor, bound);

        // The plane computes: drain until the shadowed instance settled.
        var instanceId = await PlaneInstanceIdAsync(reviewDelivery);
        await KernelOps.PollUntilAsync(
            async () =>
            {
                await dispatcher.DrainOnceAsync(CancellationToken.None);
                // Settled = the guard AND the fork leg both logged (a
                // shadowed instance row exists before the first step runs).
                return await ScalarLongAsync(
                    "SELECT count(*) FROM facilitator.workflow_step_log WHERE instance_id = $1 AND step_id IN ('guard-terminal','fork')",
                    instanceId) >= 2;
            },
            "the shadow instance settles",
            20_000);
        Assert.Equal("shadowed", await ValAsync(
            "SELECT status FROM facilitator.workflow_instances WHERE id = $1", instanceId));

        var logs = await RowsAsync(
            "SELECT jsonb_build_object('stepId', step_id, 'status', status, 'output', output) FROM facilitator.workflow_step_log WHERE instance_id = $1 ORDER BY seq",
            instanceId);
        Assert.Contains(logs, l => l.GetProperty("stepId").GetString() == "guard-terminal"
                                   && l.GetProperty("status").GetString() == "shadow"
                                   && l.GetProperty("output").GetProperty("hit").GetBoolean());
        var fork = Assert.Single(logs, l => l.GetProperty("stepId").GetString() == "fork");
        var would = fork.GetProperty("output").GetProperty("wouldDo");

        // Deterministic identity law: the plane's successor id derives
        // from (instance, step) — recomputed here from nothing but the ids.
        Assert.Equal(WorkflowIdentity.ForkWorkId(instanceId, "fork").ToString(),
            would.GetProperty("successor").GetString());
        var predecessorShort = workId.Value.ToString("N")[..8];
        Assert.StartsWith($"Follow-up to {predecessorShort}: provider input (forgejo post",
            would.GetProperty("objective").GetString());
        Assert.Contains("changes-requested review on change request #" + pr + ": guard the null case",
            would.GetProperty("objective").GetString());
        Assert.Equal("successor", would.GetProperty("linkKind").GetString());
        Assert.Equal("input arrived after the work closed", would.GetProperty("linkNote").GetString());
        Assert.Equal($"successor:{WorkflowIdentity.ForkWorkId(instanceId, "fork"):N}",
            would.GetProperty("replyCommandId").GetString());

        // The shadow writes NOTHING real: no plane command effects, one
        // successor work total (the arm's), one reply row total (the arm's).
        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT count(*) FROM facilitator.outbox WHERE command_id LIKE 'wf:%' AND event_type LIKE 'org.facilitator.command%'"));
        Assert.Equal(1L, await ScalarLongAsync(
            "SELECT count(*) FROM facilitator.outbox WHERE command_id = $1", $"successor:{armSuccessor:N}"));
        // The plane's would-be reply id (deterministic) must NOT exist as a
        // row: shadow computes, never appends.
        Assert.Equal(0L, await ScalarLongAsync(
            "SELECT count(*) FROM facilitator.outbox WHERE command_id = $1",
            $"successor:{WorkflowIdentity.ForkWorkId(instanceId, "fork"):N}"));
        // (The plane's own reply would carry the deterministic id below —
        // asserted absent by the wf: count and by the successor-work count.)
        // Scoped to THIS predecessor's short id: the collection's sibling
        // walks fork on the same shared database.
        Assert.Equal(1L, await ScalarLongAsync(
            "SELECT count(*) FROM facilitator.works WHERE objective LIKE $1", $"Follow-up to {predecessorShort}:%"));

        // The reconciliation report: keyed on the delivery identity, the
        // plane's plan matches the arm's effects (modulo the successor id
        // the arm minted randomly — the deterministic law, not a fudge).
        var reportResponse = await kernel.GetAsync("/api/admin/workflows/shadow/report?since=2020-01-01T00:00:00Z");
        var reportBody = await reportResponse.Content.ReadAsStringAsync();
        Assert.True(reportResponse.IsSuccessStatusCode, $"report failed: {reportBody}");
        using var report = JsonDocument.Parse(reportBody);
        Assert.True(report.RootElement.EnumerateArray().Any(), $"report empty; body: {reportBody}");
        var row = Assert.Single(report.RootElement.EnumerateArray(), r =>
            r.GetProperty("deliveryId").GetString() == reviewDelivery);
        Assert.True(row.GetProperty("match").GetBoolean(),
            $"shadow reconciliation mismatch: {row.GetProperty("diffs")}");
        Assert.True(row.GetProperty("planeWouldAct").GetBoolean());
        Assert.True(row.GetProperty("armActed").GetBoolean());

        // ---- shape 2: review on LIVE work (guard miss: neither side forks) ----
        var repo2 = $"repo-{WalkKit.Leaf()}";
        var pr2 = $"pr-{WalkKit.Leaf()}";
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "forgejo", "git.localhost",
                $"dlv-{WalkKit.Leaf()}", repo2, pr2, "acct-1", "review this branch"),
            HttpStatusCode.Accepted);
        var liveWork = await fixture.Db.BindingWorkId("forgejo", repo2, pr2);
        Assert.NotNull(liveWork);
        var review2Delivery = $"rev-{WalkKit.Leaf()}";
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ReviewEnvelope(repo2, pr2, review2Delivery, "approved", "looks good"),
            HttpStatusCode.Accepted);
        await KernelOps.PollUntilAsync(
            async () =>
            {
                await dispatcher.DrainOnceAsync(CancellationToken.None);
                return await ValAsync(
                    "SELECT status FROM facilitator.workflow_instances WHERE id = $1",
                    await PlaneInstanceIdAsync(review2Delivery)) is not null;
            },
            "the live-review shadow instance settles",
            20_000);
        var liveInstance = await PlaneInstanceIdAsync(review2Delivery);
        var liveLogs = await RowsAsync(
            "SELECT jsonb_build_object('stepId', step_id, 'status', status) FROM facilitator.workflow_step_log WHERE instance_id = $1",
            liveInstance);
        Assert.DoesNotContain(liveLogs, l => l.GetProperty("stepId").GetString() == "fork");

        using var report2 = JsonDocument.Parse(
            await kernel.GetStringAsync("/api/admin/workflows/shadow/report?since=2020-01-01T00:00:00Z"));
        var row2 = Assert.Single(report2.RootElement.EnumerateArray(), r =>
            r.GetProperty("deliveryId").GetString() == review2Delivery);
        Assert.True(row2.GetProperty("match").GetBoolean(),
            $"live-review reconciliation mismatch: {row2.GetProperty("diffs")}");
        Assert.False(row2.GetProperty("planeWouldAct").GetBoolean());
        Assert.False(row2.GetProperty("armActed").GetBoolean());
    }

    // ---- helpers -------------------------------------------------------------

    private static string ReviewEnvelope(string repo, string pr, string deliveryId, string state, string text)
    {
        var review = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.review.submitted.json"))!.AsObject();
        review["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = review["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = "acct-1";
        data["state"] = state;
        data["text"] = text;
        var reference = data["reference"]!.AsObject();
        reference["tenantOrOrganizationId"] = "git.localhost";
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = pr;
        return review.ToJsonString();
    }

    /// <summary>The plane's instance for one review delivery: the trigger
    /// row carries the delivery id, the instance identity derives from the
    /// row's own id (the deterministic law, recomputed not queried).</summary>
    private async Task<Guid> PlaneInstanceIdAsync(string deliveryId)
    {
        var triggerRow = await ValAsync(
            """
            SELECT outbox_id::text FROM facilitator.outbox
             WHERE recipient_endpoint_id = 'workflow.kernel'
               AND envelope::jsonb -> 'data' ->> 'providerDeliveryId' = $1
            """, deliveryId);
        Assert.NotNull(triggerRow);
        return WorkflowIdentity.InstanceIdFor("review-fork", 1, Guid.Parse(triggerRow!));
    }

    private async Task<string?> ValAsync(string sql, params object[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    private async Task<List<string>> ValsAsync(string sql, params object[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task<List<JsonElement>> RowsAsync(string sql, params object[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var rows = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return rows;
    }

    private async Task<long> ScalarLongAsync(string sql, params object[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
