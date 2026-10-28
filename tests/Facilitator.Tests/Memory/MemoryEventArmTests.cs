using System.Net;

using Facilitator.Tests.Artifacts;
using Facilitator.Tests.Infrastructure;

namespace Facilitator.Tests.Memory;

/// <summary>
/// The frozen memory event arms on the ungated CE plane (/api/ingress/events,
/// Plan 16 step 3): a candidate names a live Work, so ownership follows the
/// Work's Project→Team lineage and the entry lands promotable + timeline-noted;
/// a used attestation carries no verifiable team, so the read law denies while
/// the plane still answers accepted (§14.6: routing never blocks on memory).
/// </summary>
[Collection("postgres:work")]
public sealed class MemoryEventArmTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task CePlaneCandidateLandsPromotableUnderWorkLineageTeam()
    {
        using var factory = new KernelTestFactory(
            _fixture, noDispatcher: true, settings: MemoryTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var projectId = await _fixture.Db.SeedProject("team-alpha");
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "event arm work", projectId);

        var term = $"ce{Guid.NewGuid():N}";
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            MemoryTestKit.CandidateEnvelope("project", $"ce summary {term} stays {term} hot", workId.ToString()),
            HttpStatusCode.Accepted);

        var entryId = (await RecallEntryIdAsync(alice, term))
            ?? throw new InvalidOperationException("CE-plane candidate did not surface in recall");
        var row = await _fixture.Db.MemoryEntry(entryId);
        Assert.NotNull(row);
        Assert.Equal("candidate", row.Status);
        Assert.Equal("team-alpha", row.TeamSlug);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "memory.candidate"));

        // The lineage team's owner promotes it — the entry is fully owned.
        await KernelOps.PostJsonAsync(alice, $"/api/memory/{entryId}/promote",
            """{"targetScope":"team:team-alpha"}""", HttpStatusCode.OK);
        row = await _fixture.Db.MemoryEntry(entryId);
        Assert.Equal("active", row!.Status);
    }

    [Fact]
    public async Task CePlaneUsedDeniesWithoutTeamButNeverRefuses()
    {
        using var factory = new KernelTestFactory(
            _fixture, noDispatcher: true, settings: MemoryTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var projectId = await _fixture.Db.SeedProject("team-alpha");

        var term = $"ceused{Guid.NewGuid():N}";
        var created = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"project:{projectId}", $"attest target {term} recorded"),
            HttpStatusCode.Created);
        var entryId = Guid.Parse(created.GetProperty("entryId").GetString()!);

        // The ungated plane carries no workload team: the read law denies the
        // attestation (audit row), yet the event is still accepted.
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            MemoryTestKit.UsedEnvelope(entryId.ToString()), HttpStatusCode.Accepted);
        Assert.Equal(0, await _fixture.Db.MemoryUsageCount(entryId, "external-attestation"));
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.used" && a.Decision == "deny" && a.Resource == $"memory/{entryId}");

        // A garbage memoryId is the same accepted swallow.
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            MemoryTestKit.UsedEnvelope(Guid.NewGuid().ToString()), HttpStatusCode.Accepted);
    }

    private static async Task<Guid?> RecallEntryIdAsync(HttpClient alice, string term)
    {
        var recall = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        var entries = recall.GetProperty("entries").EnumerateArray().ToArray();
        return entries.Length == 0
            ? null
            : Guid.Parse(entries[0].GetProperty("entryId").GetString()!);
    }
}
