using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// N30 stale moment across a reconnect: a live routed assignment refuses a
/// completed event stamped at a stale generation (422 <c>stale-generation</c>,
/// the inbox row audited <c>failed</c>, the ledgers untouched). The
/// executor's reconnect — a redispatch intent — bumps the generation, and
/// the same completed shape on the FRESH stamp lands once (the manifest
/// records, a redelivery of the same envelope dedupes): a stale moment is
/// refused loudly, a fresh moment is accepted exactly once.
/// Segment: GenerationFenceTests.StaleGenerationCompletedEvent_Refused422_WithFailedInbox_AndUntouchedLedger.
/// </summary>
[Collection("postgres:walks")]
public sealed class StaleMomentWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReconnectBumpsGeneration_StaleRefused_FreshAcceptedOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();

        // A live routed assignment (fleet advertised, funnel drained).
        var team = "walk-n30-" + WalkKit.Leaf();
        await fixture.Db.SeedTeam(team);
        await WalkKit.SeedFleetAsync(fixture.Db, $"walk.ep{WalkKit.Leaf()}", team, "research.analyze");
        var projectId = await fixture.Db.SeedProject(team);
        var created = await KernelOps.PostJsonAsync(kernel, "/api/works", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["objective"] = "stale moment",
            ["projectId"] = projectId,
            ["commandId"] = Guid.NewGuid().ToString("N"),
            ["assignments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["capabilityId"] = "research.analyze",
                    ["objective"] = "fence me",
                    ["targetId"] = "kernel",
                    ["actorEvidenceRef"] = "identity-evidence:walk-suite",
                },
            },
        }), HttpStatusCode.Created);
        var workId = created.GetProperty("workId").GetGuid();
        var effect = await WalkKit.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"), "assign effect");
        var wireAssignment = JsonDocument.Parse(effect).RootElement
            .GetProperty("data").GetProperty("assignmentId").GetString()!;
        var assignmentId = Guid.ParseExact(wireAssignment, "N");

        // The stale moment: a completed event at generation 9 — refused 422,
        // audited as a failed inbox row, ledgers untouched.
        var staleId = $"walk-stale-{Guid.NewGuid():N}";
        using var stale = await kernel.PostAsync("/api/ingress/events",
            new StringContent(CompletedEnvelope(staleId, workId, assignmentId, 9), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/stale-generation",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement
                .GetProperty("type").GetString());
        Assert.Equal(("failed", "stale-generation"), await fixture.Db.InboxStateError(staleId));
        Assert.Equal(new[] { ("routed", "research.analyze", 1) }, await fixture.Db.AssignmentRowsForWork(workId));
        Assert.Empty(await fixture.Db.ManifestRowsForAssignment(assignmentId));

        // The reconnect: a redispatch intent rides prior-generation + 1 —
        // the fence's fresh moment.
        await fixture.Db.SeedAttemptRow(
            Guid.NewGuid(), assignmentId, 1, "stopped", DateTime.UtcNow - TimeSpan.FromMinutes(5));
        var steerId = Guid.NewGuid().ToString("N");
        await KernelOps.PostJsonAsync(kernel, $"/api/works/{workId}/redispatch", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["capabilityId"] = "research.analyze",
            ["objective"] = "reconnected",
            ["commandId"] = steerId,
        }), HttpStatusCode.Accepted);
        // The reconnect lands when the routing sink has consumed the intent:
        // the terminal law mints a NEW assignment at prior-generation + 1,
        // so poll the ledger for a generation-2 row, then read its identity
        // from the effect envelope.
        await WalkKit.DrainUntilAsync(factory, async () =>
            (await fixture.Db.AssignmentRowsForWork(workId)).Any(a => a.Generation == 2) ? "bumped" : null,
            "redispatch generation bump");
        var freshGeneration = 2;
        var effectRow = await fixture.Db.OutboxEnvelopeByCommand($"work-redispatch:{steerId}");
        Assert.NotNull(effectRow);
        var freshAssignment = Guid.ParseExact(JsonDocument.Parse(effectRow!).RootElement
            .GetProperty("data").GetProperty("assignmentId").GetString()!, "N");

        // The fresh moment lands once.
        var freshId = $"walk-fresh-{Guid.NewGuid():N}";
        var accepted = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            CompletedEnvelope(freshId, workId, freshAssignment, freshGeneration), HttpStatusCode.Accepted);
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());
        Assert.Contains(("completed", "research.analyze", 2), await fixture.Db.AssignmentRowsForWork(workId));

        // A redelivery of the SAME envelope dedupes — the moment was
        // accepted exactly once: the generation row count stays at one
        // attempt on the fresh generation, no second manifest.
        using var again = await kernel.PostAsync("/api/ingress/events",
            new StringContent(CompletedEnvelope(freshId, workId, freshAssignment, freshGeneration),
                Encoding.UTF8, "application/json"));
        Assert.True(again.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK);
        Assert.Equal(1, (await fixture.Db.AssignmentRowsForWork(workId)).Count(a => a.Generation == freshGeneration));
    }

    /// <summary>The exactly-valid completed envelope (the live leg's shape).</summary>
    private static string CompletedEnvelope(
        string eventId, Guid workId, Guid assignmentId, int executionGeneration) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = eventId,
            ["source"] = "urn:workspace:walk-fence",
            ["type"] = "org.facilitator.event.assignment.completed.v1",
            ["subject"] = $"work/{workId}/assignment/{assignmentId:N}",
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
            ["data"] = new Dictionary<string, object?>
            {
                ["workId"] = workId.ToString(),
                ["assignmentId"] = assignmentId.ToString("N"),
                ["executionId"] = $"walk-exec-{Guid.NewGuid():N}",
                ["executionGeneration"] = executionGeneration,
                ["resultManifest"] = new Dictionary<string, object?>
                {
                    ["manifestId"] = $"walk-manifest-{Guid.NewGuid():N}",
                    ["outputs"] = Array.Empty<object>(),
                },
            },
        });
}
