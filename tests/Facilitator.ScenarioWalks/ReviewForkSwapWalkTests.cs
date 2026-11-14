using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// The swap gate (Phase 3): the review-fork behavior, executed twice —
/// once by the ARM (plane runtime off: the old path, byte-identical to
/// before the plane existed) and once by the PLANE (review-fork
/// enabled-and-live: the arm defers, WorkFork.ApplyAsync performs the
/// fork) — on the same database, then compared field by field. Successor
/// fields, timeline kinds/actors/details, link kind/note, binding
/// repoint, reply command id + sentence, progress echo: identical modulo
/// the per-run identity (random guid on the arm side, deterministic
/// UUIDv5(instance:step) on the plane side — and the plane's judgment
/// names the deterministic successor up-front, the receipt is honest
/// pre-delivery). The plane run is a TIMELINE SUPERSET: exactly one
/// extra row, kind <c>workflow.stage</c> — the superset law's proof, not
/// a gate-breaker.
/// Segments: ReviewForkWalkTests (arm posture) + ReviewForkShadowWalkTests
/// (plane posture, manual drains).
/// </summary>
[Collection("postgres:walks")]
public sealed class ReviewForkSwapWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PlaneForkIsArmForkPlusStageRow()
    {
        var endpoint = "gw-swap-" + WalkKit.Leaf();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
        });
        var kernel = factory.CreateAuthedClient();
        var link = factory.LinkClient(await WalkKit.EnrollAsync(kernel, endpoint));
        var runtime = factory.Services.GetRequiredService<WorkflowRuntime>();
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        await fixture.Db.SeedProviderIdentity("forgejo", "git.localhost", "acct-1", "subject-1");

        // ---- run A: the ARM performs the fork (plane runtime off) ----
        runtime.Enabled = false;
        var (repoA, prA, _, workA, successorA, _) =
            await PostReviewOnTerminalWorkAsync(link, kernel, "guard the null case");
        var (boundA, _) = await fixture.Db.BindingFor("forgejo", repoA, prA);
        Assert.Equal(successorA, boundA); // the arm's law still holds as the baseline
        var timelineA = await TimelineAsync([workA, successorA]);
        var replyA = await ReplyRowAsync(successorA);
        var progressA = await ProgressRowsAsync(successorA);
        var idsA = new List<Guid> { workA, successorA };

        // ---- run B: the PLANE performs the fork (enabled, not shadowed) ----
        runtime.Enabled = true;
        await KernelOps.PostJsonAsync(kernel,
            "/api/admin/workflows/definitions/review-fork/versions/1/toggle",
            """{"enabled": true, "shadow": false}""", HttpStatusCode.OK, "phase 3 swap walk");
        try
        {
            var (repoB, prB, _, workB, successorB, judgmentB) =
                await PostReviewOnTerminalWorkAsync(link, kernel, "guard the null case");

            // The deferral law: the judgment names the deterministic
            // successor BEFORE the plane runs — recomputed here from the
            // trigger row's identity, independent of the plane.
            var triggerRow = Guid.Parse((await ValAsync(
                """
                SELECT outbox_id::text FROM facilitator.outbox
                 WHERE event_type = 'org.facilitator.event.provider.review.v1'
                   AND recipient_endpoint_id = 'workflow.kernel'
                 ORDER BY created_at DESC LIMIT 1
                """))!);
            var planeInstance = WorkflowIdentity.InstanceIdFor("review-fork", 1, triggerRow);
            Assert.Equal(WorkflowIdentity.ForkWorkId(planeInstance, "fork"), successorB);
            Assert.Equal($"work/{successorB}:input", judgmentB);

            // Plane executes: drain until the instance completes.
            await KernelOps.PollUntilAsync(
                async () =>
                {
                    await dispatcher.DrainOnceAsync(CancellationToken.None);
                    return await ScalarLongAsync(
                        """
                        SELECT count(*) FROM facilitator.workflow_instances
                         WHERE def_id = 'review-fork' AND status = 'completed'
                           AND primary_work_id = $1
                        """, workB) == 1;
                },
                "the plane completes the fork",
                20_000);

            var timelineB = await TimelineAsync([workB, successorB]);
            var replyB = await ReplyRowAsync(successorB);
            var progressB = await ProgressRowsAsync(successorB);
            var idsB = new List<Guid> { workB, successorB, planeInstance };

            // Superset law: the plane timeline = the arm timeline +
            // exactly one workflow.stage row; everything else normalizes
            // to the same multiset of rows.
            var stageRows = timelineB.Where(r => r.GetProperty("kind").GetString() == "workflow.stage").ToList();
            Assert.Single(stageRows);
            var withoutStage = timelineB
                .Where(r => r.GetProperty("kind").GetString() != "workflow.stage")
                .Select(r => Normalize(r.GetRawText(), "run-B", idsB))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            var normalizedA = timelineA
                .Select(r => Normalize(r.GetRawText(), "run-A", idsA))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            Assert.True(normalizedA.SequenceEqual(withoutStage),
                "timeline mismatch:\nA: " + string.Join("\nA: ", normalizedA) + "\nB: " + string.Join("\nB: ", withoutStage));

            // Reply: same command-id prefix law, same recipient, the same
            // sentence modulo the successor short id.
            Assert.StartsWith("successor:", replyA.GetProperty("commandId").GetString());
            Assert.StartsWith("successor:", replyB.GetProperty("commandId").GetString());
            Assert.Equal(replyA.GetProperty("recipient").GetString(), replyB.GetProperty("recipient").GetString());
            // The sentence law, shape-checked: BOTH short ids (the
            // predecessor's too) are per-run by nature.
            foreach (var reply in new[] { replyA, replyB })
            {
                Assert.Matches(
                    @"^Work [0-9a-f]{8} is closed - this continues as work [0-9a-f]{8}\.$",
                    reply.GetProperty("text").GetString());
            }

            // Progress echo: one row, same type + recipient + note (note
            // normalized per run: it names the conversation).
            var noteA = Assert.Single(progressA);
            var noteB = Assert.Single(progressB);
            Assert.Equal(noteA.GetProperty("type").GetString(), noteB.GetProperty("type").GetString());
            Assert.Equal(noteA.GetProperty("recipient").GetString(), noteB.GetProperty("recipient").GetString());
            Assert.Equal(
                CrossBlind(Normalize(noteA.GetProperty("note").GetString()!, "run-A", idsA)),
                CrossBlind(Normalize(noteB.GetProperty("note").GetString()!, "run-B", idsB)));

            // Binding follows the live item in the plane run too.
            var (boundB, _) = await fixture.Db.BindingFor("forgejo", repoB, prB);
            Assert.Equal(successorB, boundB);
        }
        finally
        {
            // Restore the gallery default: other walks assume review-fork
            // disabled (the arm owns the behavior by default).
            await KernelOps.PostJsonAsync(kernel,
                "/api/admin/workflows/definitions/review-fork/versions/1/toggle",
                """{"enabled": false, "shadow": false}""", HttpStatusCode.OK, "phase 3 swap walk: restore");
        }
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<(string Repo, string Pr, string Delivery, Guid Work, Guid Successor, string Judgment)> PostReviewOnTerminalWorkAsync(
        HttpClient link, HttpClient kernel, string text)
    {
        var leaf = WalkKit.Leaf();
        var repo = $"repo-{leaf}";
        var pr = $"pr-{leaf}";
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "forgejo", "git.localhost",
                $"dlv-{leaf}", repo, pr, "acct-1", "review this branch"),
            HttpStatusCode.Accepted);
        var workId = (await fixture.Db.BindingFor("forgejo", repo, pr)).WorkId;
        Assert.NotNull(workId);
        await KernelOps.TransitionAsync(kernel, workId.Value, "planning", 1);
        await KernelOps.TransitionAsync(kernel, workId.Value, "cancelled", 2);

        var delivery = $"rev-{leaf}";
        var review = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.review.submitted.json"))!.AsObject();
        review["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = review["data"]!.AsObject();
        data["providerDeliveryId"] = delivery;
        data["actorProviderAccountId"] = "acct-1";
        data["text"] = text;
        var reference = data["reference"]!.AsObject();
        reference["tenantOrOrganizationId"] = "git.localhost";
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = pr;
        var receipt = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            review.ToJsonString(), HttpStatusCode.Accepted);
        var judgment = receipt.GetProperty("data").GetProperty("judgment").GetString()!;
        Assert.StartsWith("work/", judgment);
        var successor = Guid.Parse(judgment.AsSpan("work/".Length, 36));
        return (repo, pr, delivery, workId.Value, successor, judgment);
    }

    private async Task<List<JsonElement>> TimelineAsync(Guid[] workIds)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT jsonb_build_object('kind', kind, 'actor', actor, 'detail', detail::text)
            FROM facilitator.work_events WHERE work_id = ANY($1::uuid[])
            """, connection);
        command.Parameters.AddWithValue(workIds);
        var rows = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return rows;
    }

    private async Task<JsonElement> ReplyRowAsync(Guid successor) =>
        Assert.Single(await RowsAsync(
            """
            SELECT jsonb_build_object('commandId', command_id, 'recipient', recipient_endpoint_id,
                                      'text', envelope::jsonb -> 'data' ->> 'text')
            FROM facilitator.outbox WHERE command_id = $1
            """, $"successor:{successor:N}"));

    private async Task<List<JsonElement>> ProgressRowsAsync(Guid successor) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('type', event_type, 'recipient', recipient_endpoint_id,
                                      'note', envelope::jsonb -> 'data' ->> 'progressNote')
            FROM facilitator.outbox WHERE event_type = 'org.facilitator.event.work.progress.v1'
              AND aggregate_id = $1
            """, successor);

    // Per-run identity normalization: the two runs differ ONLY in these
    // tokens — every other byte of a timeline row must match.
    private static string Normalize(string text, string runToken, IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            text = text
                .Replace(id.ToString(), runToken)
                .Replace(id.ToString("N"), runToken)
                .Replace(id.ToString("N")[..8], runToken);
        }

        // The leaf tokens appear BOTH as whole values and mid-sentence
        // inside detail JSON: blind them anywhere.
        return System.Text.RegularExpressions.Regex.Replace(
            text, "(repo|pr|rev|dlv)-[0-9a-f]+|evt-[0-9a-f-]+|run-[AB]", "@");
    }

    private static string CrossBlind(string normalized) =>
        normalized.Replace("run-A", "@").Replace("run-B", "@");

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
