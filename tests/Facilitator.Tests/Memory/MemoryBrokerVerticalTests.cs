using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Shared.Memory;
using Facilitator.Tests.Artifacts;

namespace Facilitator.Tests.Memory;

/// <summary>
/// The memory vertical: a human retains over REST (lands <c>active</c>,
/// D4), the row + hash + audit ride the broker's tx, recall answers the D8 shape
/// and writes the usage rows, content dedupe returns the stored identity,
/// and a foreign team sees nothing (no count, no existence hint).
/// </summary>
[Collection("postgres:work")]
public sealed class MemoryBrokerVerticalTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task RestRetainRecallWriteLedgerAndUsageRows()
    {
        using var factory = Host();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var projectId = await _fixture.Db.SeedProject("team-alpha");
        var term = $"rot{Guid.NewGuid():N}";

        var created = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"project:{projectId}", $"token rotation runs at {term} utc nightly"),
            HttpStatusCode.Created);
        var entryId = Guid.Parse(created.GetProperty("entryId").GetString()!);
        Assert.Equal("active", created.GetProperty("status").GetString());
        Assert.False(created.GetProperty("duplicate").GetBoolean());

        var row = await _fixture.Db.MemoryEntry(entryId);
        Assert.NotNull(row);
        Assert.Equal("team-alpha", row.TeamSlug);
        Assert.Equal("active", row.Status);
        Assert.Equal(MemoryTestKit.Sha256Of($"token rotation runs at {term} utc nightly"), row.ContentHash);
        Assert.True(row.Indexed);
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.retained" && a.Resource == $"memory/{entryId}" && a.Decision == "allow");

        // Recall: D8 shape, entry present, usage law: one row per entry.
        var recall = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        var entry = Assert.Single(recall.GetProperty("entries").EnumerateArray());
        Assert.Equal(entryId, Guid.Parse(entry.GetProperty("entryId").GetString()!));
        Assert.Equal("team-only", entry.GetProperty("policyLabel").GetString());
        Assert.False(recall.GetProperty("truncated").GetBoolean());
        Assert.Equal("postgres-fts", recall.GetProperty("providerProfile").GetString());
        Assert.Equal(1, await _fixture.Db.MemoryUsageCount(entryId));
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.recall" && a.Decision == "allow");
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.used" && a.Decision == "allow");

        // Dedupe (D9): same content, same scope — stored identity, no new row.
        var again = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"project:{projectId}", $"token rotation runs at {term} utc nightly"),
            HttpStatusCode.Created);
        Assert.Equal(entryId, Guid.Parse(again.GetProperty("entryId").GetString()!));
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, await _fixture.Db.MemoryUsageCount(entryId));

        // A query nothing matches: zero entries, honest truncation flag.
        var miss = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody($"nothing{Guid.NewGuid():N}"), HttpStatusCode.OK);
        Assert.Empty(miss.GetProperty("entries").EnumerateArray());
        Assert.False(miss.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CrossTeamSeesNothingUntilSharedThenNothingAgain()
    {
        using var factory = Host();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var bob = await ArtifactTestKit.UserClientAsync(factory, "bob");
        var projectId = await _fixture.Db.SeedProject("team-alpha");
        var term = $"acl{Guid.NewGuid():N}";

        var created = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"project:{projectId}", $"nightly drain keeps {term} warm"),
            HttpStatusCode.Created);
        var entryId = Guid.Parse(created.GetProperty("entryId").GetString()!);

        // Bob (team-beta): zero rows, and the body carries no count/existence field.
        var bobRecall = await KernelOps.PostJsonAsync(bob, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        Assert.Empty(bobRecall.GetProperty("entries").EnumerateArray());
        Assert.DoesNotContain("\"total\"", bobRecall.ToString());
        var bobGet = await bob.GetAsync($"/api/memory/{entryId}");
        Assert.Equal(HttpStatusCode.NotFound, bobGet.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/memory-not-found",
            MemoryTestKit.ProblemType(bobGet));

        // Alice shares to team-beta: bob recalls it and GETs it.
        var share = await KernelOps.PostJsonAsync(alice, $"/api/memory/{entryId}/shares",
            """{"granteeTeam":"team-beta"}""", HttpStatusCode.OK);
        var shareId = Guid.Parse(share.GetProperty("entryId").GetString()!);
        Assert.Equal(entryId, shareId);
        var shared = await KernelOps.GetJsonAsync(alice, $"/api/memory/{entryId}", HttpStatusCode.OK);
        var liveShare = Assert.Single(shared.GetProperty("shares").EnumerateArray());

        var bobSees = await KernelOps.PostJsonAsync(bob, "/api/memory/recall",
            MemoryTestKit.RecallBody(term), HttpStatusCode.OK);
        var seen = Assert.Single(bobSees.GetProperty("entries").EnumerateArray());
        Assert.Equal(entryId, Guid.Parse(seen.GetProperty("entryId").GetString()!));
        Assert.True(seen.TryGetProperty("provenance", out _));
        await KernelOps.GetJsonAsync(bob, $"/api/memory/{entryId}", HttpStatusCode.OK);

        // Revoke: the same non-disclosing 404 as before — nothing new learned.
        var revoked = await alice.DeleteAsync(
            $"/api/memory/{entryId}/shares/{liveShare.GetProperty("shareId").GetString()}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var afterRevoke = await bob.GetAsync($"/api/memory/{entryId}");
        Assert.Equal(HttpStatusCode.NotFound, afterRevoke.StatusCode);
        Assert.Equal(0, await _fixture.Db.MemoryShareCount(entryId, "team-beta"));
    }

    [Fact]
    public async Task WorkSelectorOnProjectlessWorkLandsInCallerTeamLineage()
    {
        // The work-selector's team join materializes a NULLABLE slug: a work
        // with no project falls into the caller's team scope lineage (the
        // documented law), instead of faulting on the missing join.
        using var factory = Host();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var (workId, _) = await KernelOps.CreateWorkAsync(factory.CreateAuthedClient(), "projectless retain");
        var term = $"wls{Guid.NewGuid():N}";

        var created = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"work:{workId}", $"retained against a project-less work {term}"),
            HttpStatusCode.Created);
        var entryId = Guid.Parse(created.GetProperty("entryId").GetString()!);

        var row = await _fixture.Db.MemoryEntry(entryId);
        Assert.NotNull(row);
        Assert.Equal("team-alpha", row.TeamSlug);
    }

    private KernelTestFactory Host() =>
        new(_fixture, noDispatcher: true, settings: MemoryTestKit.WorkloadBinding);
}
