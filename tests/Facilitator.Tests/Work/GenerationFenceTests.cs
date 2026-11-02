using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Work;

/// <summary>
/// The §7.4 generation fence: a terminal completed event whose
/// stamped <c>executionGeneration</c> is not the assignment's
/// <c>current_execution_generation</c> is REFUSED — 422 <c>stale-generation</c>,
/// the inbox row settles <c>failed</c> with the fence error, and the
/// assignment/manifest ledgers stay untouched. A byte-identical redelivery
/// dedupes on the inbox ledger; a fresh-envelope redelivery of the same stale
/// stamp fences identically (the fence is a pure ledger read). The operator
/// redispatch route rides the NEXT generation: the intent envelope carries
/// max(prior attempt generation) + 1, so a late completion from the fenced-
/// over generation can never settle the new execution.
/// Hermeticity mirrors the routing suite: own team, GUID-scoped endpoints
/// (<c>gf.</c> prefix, purged on dispose), polled drains.
/// </summary>
[Collection("postgres:work")]
public sealed class GenerationFenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() =>
        // A leaked live fleet would join a later run's candidate set.
        fixture.Db.PurgeFleets("gf.");

    [Fact]
    public async Task StaleGenerationCompletedEvent_Refused422_WithFailedInbox_AndUntouchedLedger()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (workId, assignmentId) = await RoutedAssignmentAsync(factory, kernel);

        var eventId = $"gf-stale-{Guid.NewGuid():N}";
        using var response = await kernel.PostAsync("/api/ingress/events",
            new StringContent(CompletedEnvelope(eventId, workId, assignmentId, 9), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/stale-generation", problem.GetProperty("type").GetString());
        var detail = problem.GetProperty("detail").GetString() ?? string.Empty;
        Assert.Contains("generation 9", detail, StringComparison.Ordinal);
        Assert.Contains("generation 1", detail, StringComparison.Ordinal);

        // The refusal is audited: the inbox row settles failed with the fence error.
        var inbox = await fixture.Db.InboxStateError(eventId);
        Assert.NotNull(inbox);
        Assert.Equal("failed", inbox!.Value.State);
        Assert.Equal("stale-generation", inbox.Value.Error);

        // The ledgers the fenced event would have written stay untouched:
        // the assignment sits on its routed first generation, no manifest row.
        Assert.Equal(new[] { ("routed", "research.analyze", 1) }, await fixture.Db.AssignmentRowsForWork(workId));
        Assert.Empty(await fixture.Db.ManifestRowsForAssignment(assignmentId));
    }

    [Fact]
    public async Task StaleEventRedelivery_FencesIdentically_AndSameEnvelopeDedupes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (workId, assignmentId) = await RoutedAssignmentAsync(factory, kernel);

        var eventId = $"gf-stale-{Guid.NewGuid():N}";
        var envelope = CompletedEnvelope(eventId, workId, assignmentId, 9);
        using var first = await kernel.PostAsync("/api/ingress/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);

        // Byte-identical replay: the inbox dedupe swallows it — no re-write.
        var replay = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());

        // A redelivery of the same stale stamp under a fresh envelope id
        // fences identically (422 + a second failed inbox row): the fence is
        // a pure ledger read, never a one-shot guard.
        var secondId = $"gf-stale-{Guid.NewGuid():N}";
        using var again = await kernel.PostAsync("/api/ingress/events",
            new StringContent(CompletedEnvelope(secondId, workId, assignmentId, 9), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(again.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/stale-generation", problem.GetProperty("type").GetString());

        Assert.Equal(("failed", "stale-generation"), await fixture.Db.InboxStateError(eventId));
        Assert.Equal(("failed", "stale-generation"), await fixture.Db.InboxStateError(secondId));
        Assert.Equal(new[] { ("routed", "research.analyze", 1) }, await fixture.Db.AssignmentRowsForWork(workId));
        Assert.Empty(await fixture.Db.ManifestRowsForAssignment(assignmentId));
    }

    [Fact]
    public async Task RedispatchIntent_RidesNextGenerationAfterTerminalAttempt()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (workId, assignmentId) = await RoutedAssignmentAsync(factory, kernel);

        // A fenced-over prior execution: the work's latest attempt row is
        // terminal at generation 3 (cheap seed instead of three live legs).
        await fixture.Db.SeedAttemptRow(
            Guid.NewGuid(), assignmentId, 3, "stopped", DateTime.UtcNow - TimeSpan.FromMinutes(30));

        var steerId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["capabilityId"] = "research.analyze",
            ["objective"] = "retry with context",
            ["commandId"] = steerId,
        });
        var requested = await KernelOps.PostJsonAsync(
            kernel, $"/api/works/{workId}/redispatch", body, HttpStatusCode.Accepted);
        Assert.Equal("redispatch-requested", requested.GetProperty("status").GetString());

        // The new intent envelope rides prior-generation + 1, keyed by the
        // caller's commandId on both the intent and the effect ledger.
        var intent = await fixture.Db.OutboxEnvelopeByCommand($"routing:work-redispatch:{steerId}");
        Assert.NotNull(intent);
        using var doc = JsonDocument.Parse(intent!);
        Assert.Equal($"work/{workId}/routing/work-redispatch:{steerId}", doc.RootElement.GetProperty("subject").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("work-redispatch", data.GetProperty("commandIdBase").GetString());
        Assert.Equal(steerId, data.GetProperty("effectKey").GetString());
        Assert.Equal(4, data.GetProperty("executionGeneration").GetInt32());
    }

    // --- plumbing -------------------------------------------------------------

    private static string Leaf() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Own-team fleet advertising the capability, with a live lease
    /// (the funnel's candidate law), so one create routes exactly one assign.</summary>
    private async Task<string> FreshAdvertisedTeamAsync(HttpClient kernel)
    {
        var slug = $"gf-w-{Leaf()}";
        await fixture.Db.SeedTeam(slug);
        await FleetHarness.SeedFleetAsync(fixture.Db, $"gf.ep{Leaf()}", slug, "research.analyze");
        return slug;
    }

    /// <summary>Create a one-assignment work, drain the funnel, and return the
    /// routed assignment's deterministic id (the N-token the assign envelope
    /// carries, which the completed event must name).</summary>
    private async Task<(Guid WorkId, Guid AssignmentId)> RoutedAssignmentAsync(
        KernelTestFactory factory, HttpClient kernel)
    {
        var team = await FreshAdvertisedTeamAsync(kernel);
        var projectId = await fixture.Db.SeedProject(team);
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["objective"] = "generation fence",
            ["projectId"] = projectId,
            ["commandId"] = Guid.NewGuid().ToString("N"),
            ["assignments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["capabilityId"] = "research.analyze",
                    ["objective"] = "fence me",
                    ["targetId"] = "kernel",
                    ["actorEvidenceRef"] = "identity-evidence:fence-suite",
                },
            },
        });
        var created = await KernelOps.PostJsonAsync(kernel, "/api/works", body, HttpStatusCode.Created);
        var workId = created.GetProperty("workId").GetGuid();

        var effect = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"), "assign effect");
        var assignmentN = JsonDocument.Parse(effect).RootElement
            .GetProperty("data").GetProperty("assignmentId").GetString()!;
        return (workId, Guid.ParseExact(assignmentN, "N"));
    }

    /// <summary>An exactly-valid completed envelope (the live leg's shape):
    /// inline <c>resultManifest</c> (manifestId + empty outputs), subject
    /// <c>work/&lt;workId&gt;/assignment/&lt;assignmentN&gt;</c>, stamped at
    /// <paramref name="executionGeneration"/>.</summary>
    private static string CompletedEnvelope(
        string eventId, Guid workId, Guid assignmentId, int executionGeneration) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = eventId,
            ["source"] = "urn:workspace:gf-fence",
            ["type"] = "org.facilitator.event.assignment.completed.v1",
            ["subject"] = $"work/{workId}/assignment/{assignmentId:N}",
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
            ["data"] = new Dictionary<string, object?>
            {
                ["workId"] = workId.ToString(),
                ["assignmentId"] = assignmentId.ToString("N"),
                ["executionId"] = $"gf-exec-{Guid.NewGuid():N}",
                ["executionGeneration"] = executionGeneration,
                ["resultManifest"] = new Dictionary<string, object?>
                {
                    ["manifestId"] = $"gf-manifest-{Guid.NewGuid():N}",
                    ["outputs"] = Array.Empty<object>(),
                },
            },
        });
}
