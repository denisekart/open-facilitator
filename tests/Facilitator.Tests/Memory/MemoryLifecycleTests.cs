using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Tests.Artifacts;
using Facilitator.Tests.Infrastructure;

namespace Facilitator.Tests.Memory;

/// <summary>
/// The frozen candidate lifecycle over both planes: a workload
/// retain lands <c>candidate</c> (D4) with the receipt in-band, a candidate
/// event records entry + timeline, promote flips to <c>active</c> (REST
/// curation only), correct supersedes in a revision chain, delete tombstones
/// without disclosure, and the <c>memory.used</c> attestation leg writes the
/// usage row for readers and audits a deny for strangers.
/// </summary>
[Collection("postgres:work")]
public sealed class MemoryLifecycleTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task CandidateEventLinksTimelineAndAttestationLegsRecordUsage()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var projectId = await _fixture.Db.SeedProject("team-alpha");
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "memory lifecycle", projectId);
        var link = await LinkAsync(factory, kernel);

        // The frozen candidate event lands a candidate for the work's project
        // lineage and notes the timeline (event plane never refuses on memory).
        var term = $"cand{Guid.NewGuid():N}";
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            MemoryTestKit.CandidateEnvelope("project", $"candidate summary {term} keeps {term} hot", workId.ToString()),
            HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "memory.candidate"));

        // The used-attestation leg: owner-team reader records the usage row...
        var entryId = (await RecallEntryIdAsync(link, term))
            ?? throw new InvalidOperationException("candidate did not surface in recall");
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            MemoryTestKit.UsedEnvelope(entryId.ToString()), HttpStatusCode.Accepted);

        // ...a foreign-team workload audits a deny and the plane still accepts.
        var betaToken = await LinkTestHarness.EnrollAsync(kernel, $"mem.beta{Guid.NewGuid():N}", team: "team-beta");
        var beta = factory.CreateLinkClient(betaToken);
        var foreign = await KernelOps.PostJsonAsync(beta, "/api/link/v1/events",
            MemoryTestKit.UsedEnvelope(entryId.ToString()), HttpStatusCode.Accepted);
        Assert.Equal("accepted", foreign.GetProperty("status").GetString());
        // Only the owner-team attestation wrote a usage row (the foreign one
        // audited a deny); the recall probe's own row carries the recall purpose.
        Assert.Equal(1, await _fixture.Db.MemoryUsageCount(entryId, "external-attestation"));
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.used" && a.Decision == "deny" && a.Resource == $"memory/{entryId}");
    }

    [Fact]
    public async Task LinkRetainLandsCandidateThenPromoteCorrectAndDelete()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var link = await LinkAsync(factory, kernel);
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");

        // D4: a workload retain lands candidate with the receipt in-band.
        var term = $"life{Guid.NewGuid():N}";
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            MemoryTestKit.RetainEnvelope(
                $"cmd-mem-{Guid.NewGuid():N}", "team:team-alpha", $"nightly {term} rotation law", "identity-evidence:ref-9"),
            HttpStatusCode.Accepted);
        var data = ack.GetProperty("data");
        var entryId = Guid.Parse(data.GetProperty("entryId").GetString()!);
        Assert.Equal("candidate", data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("duplicate").GetBoolean());
        var row = await _fixture.Db.MemoryEntry(entryId);
        Assert.NotNull(row);
        Assert.Equal("team-alpha", row.TeamSlug);

        // Same commandId + content under a fresh envelope: the inbox answers
        // duplicate and re-attaches the stored receipt (duplicate:true) — the
        // retain re-run converges on the content-hash dedupe.
        var replayEnvelope = MemoryTestKit.RetainEnvelope(
            "cmd-mem-replay-fixed", "team:team-alpha", $"nightly {term} rotation law", "identity-evidence:ref-9");
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", replayEnvelope, HttpStatusCode.Accepted);
        var replay = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", replayEnvelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());
        Assert.True(replay.GetProperty("data").GetProperty("duplicate").GetBoolean());
        Assert.Equal(entryId, Guid.Parse(replay.GetProperty("data").GetProperty("entryId").GetString()!));

        // Promote: owner team's REST curation flips candidate → active,
        // clearing the candidate clock.
        await KernelOps.PostJsonAsync(alice, $"/api/memory/{entryId}/promote",
            """{"targetScope":"team:team-alpha"}""", HttpStatusCode.OK);
        row = await _fixture.Db.MemoryEntry(entryId);
        Assert.Equal("active", row!.Status);
        Assert.Equal(string.Empty, row.ExpiresAt);
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.promoted" && a.Resource == $"memory/{entryId}");

        // Wrong promote target: the typed 422 (entry real, target contradicts).
        var badTarget = await alice.PostAsync($"/api/memory/{entryId}/promote",
            new StringContent("""{"targetScope":"team:team-beta"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badTarget.StatusCode);

        // Correct: a revision-2 row supersedes; default recall hides the old
        // head, includeSuperseded surfaces the chain.
        await KernelOps.PostJsonAsync(alice, $"/api/memory/{entryId}/correct",
            $$"""{"content":"nightly {{term}} rotation law revised","contentSummary":"revised"}""",
            HttpStatusCode.OK);
        var old = await _fixture.Db.MemoryEntry(entryId);
        Assert.Equal("superseded", old!.Status);
        var chain = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term, includeSuperseded: true), HttpStatusCode.OK);
        var revisions = chain.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("revision").GetInt32()).Order().ToArray();
        Assert.Equal([1, 2], revisions);
        var defaultRecall = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        var head = Assert.Single(defaultRecall.GetProperty("entries").EnumerateArray());
        Assert.Equal(2, head.GetProperty("revision").GetInt32());
        var newId = Guid.Parse(head.GetProperty("entryId").GetString()!);
        Assert.NotEqual(entryId, newId);
        var newRow = await _fixture.Db.MemoryEntry(newId);
        Assert.Equal(entryId.ToString(), newRow!.SupersedesEntryId);

        // Delete: 204, non-disclosure after, tombstone keeps the audit trail.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/memory/{newId}")).StatusCode);
        var gone = await alice.GetAsync($"/api/memory/{newId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/memory-not-found", MemoryTestKit.ProblemType(gone));
        var tomb = await _fixture.Db.MemoryEntry(newId);
        Assert.Equal("tombstoned", tomb!.Status);
        Assert.Equal(string.Empty, tomb.Content);
        // Default recall: the tombstoned head left the index (content drop),
        // the superseded ancestor stays out of the default view.
        var afterDelete = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        Assert.Empty(afterDelete.GetProperty("entries").EnumerateArray());
    }

    private KernelTestFactory Host() =>
        new(_fixture, noDispatcher: true, settings: MemoryTestKit.WorkloadBinding);

    private static async Task<HttpClient> LinkAsync(KernelTestFactory factory, HttpClient kernel) =>
        factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "mem.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

    private static async Task<Guid?> RecallEntryIdAsync(HttpClient link, string term)
    {
        var recall = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            MemoryTestKit.RecallEnvelope($"cmd-mem-q-{Guid.NewGuid():N}", term), HttpStatusCode.Accepted);
        var entries = recall.GetProperty("data").GetProperty("entries").EnumerateArray().ToArray();
        return entries.Length == 0
            ? null
            : Guid.Parse(entries[0].GetProperty("entryId").GetString()!);
    }
}
