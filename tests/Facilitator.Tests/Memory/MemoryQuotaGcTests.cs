using System.Net;

using Facilitator.Kernel.Shared.Memory;

using Facilitator.Tests.Artifacts;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Memory;

/// <summary>
/// The D9 write guards and the §14.6 retention pass: a per-team daily budget
/// answers the typed 422 + audit deny, and an expired candidate tombstones
/// (content drop ⇒ out of every search, shares revoked, <c>memory.gc</c>
/// audited) under the second hosted loop's explicit pass.
/// </summary>
[Collection("postgres:work")]
public sealed class MemoryQuotaGcTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task DailyBudgetRefusesThirdRetainWithTypedProblem()
    {
        using var factory = new KernelTestFactory(
            _fixture, noDispatcher: true, settings: MemorySetting("Quota:DailyRetainPerTeam", "2"));
        // team-beta keeps this leg's day-count honest: the other memory
        // suites (same serial collection, same UTC day) only own team-alpha rows.
        var bob = await ArtifactTestKit.UserClientAsync(factory, "bob");

        for (var i = 0; i < 2; i++)
        {
            await KernelOps.PostJsonAsync(bob, "/api/memory/retain",
                MemoryTestKit.RetainBody("team:team-beta", $"budget probe {Guid.NewGuid():N}"),
                HttpStatusCode.Created);
        }

        var third = await bob.PostAsync("/api/memory/retain",
            new StringContent(
                MemoryTestKit.RetainBody("team:team-beta", $"budget probe {Guid.NewGuid():N}"),
                System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/memory-quota-exceeded",
            MemoryTestKit.ProblemType(third));
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.quota-exceeded" && a.Decision == "deny");
    }

    [Fact]
    public async Task ExpiredCandidateTombstonesOutOfEverySearch()
    {
        using var factory = Host();
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var gc = factory.Services.GetRequiredService<MemoryRetentionService>();
        var projectId = await _fixture.Db.SeedProject("team-alpha");
        var term = $"gc{Guid.NewGuid():N}";

        var created = await KernelOps.PostJsonAsync(alice, "/api/memory/retain",
            MemoryTestKit.RetainBody($"project:{projectId}", $"candidate {term} ages out"),
            HttpStatusCode.Created);
        var entryId = Guid.Parse(created.GetProperty("entryId").GetString()!);
        await KernelOps.PostJsonAsync(alice, $"/api/memory/{entryId}/shares",
            """{"granteeTeam":"team-beta"}""", HttpStatusCode.OK);

        await _fixture.Db.ExpireMemoryEntry(entryId);
        await gc.RunOnceAsync(default);

        var row = await _fixture.Db.MemoryEntry(entryId);
        Assert.Equal("tombstoned", row!.Status);
        Assert.Equal(string.Empty, row.Content);
        Assert.Equal(0, await _fixture.Db.MemoryShareCount(entryId, "team-beta"));
        var recall = await KernelOps.PostJsonAsync(alice, "/api/memory/recall",
            MemoryTestKit.RecallBody(term, includeSuperseded: true), HttpStatusCode.OK);
        Assert.Empty(recall.GetProperty("entries").EnumerateArray());
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "memory.gc" && a.Resource == $"memory/{entryId}" && a.Reason == "retention");

        // Second pass: the row is already tombstoned — a clean no-op.
        await gc.RunOnceAsync(default);
        Assert.Equal("tombstoned", (await _fixture.Db.MemoryEntry(entryId))!.Status);
    }

    [Fact]
    public async Task UnknownProviderFailsComposition()
    {
        // The options law: an unshipped profile throws at composition, never
        // silently falls back (the artifact-options precedent).
        using var factory = new KernelTestFactory(
            _fixture, noDispatcher: true, settings: MemorySetting("Provider", "hindsight"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.FromResult(factory.Services.GetRequiredService<IMemoryProvider>()));
    }

    private KernelTestFactory Host() =>
        new(_fixture, noDispatcher: true, settings: MemorySetting("Gc:IntervalSeconds", "3600"));

    /// <summary>The workload binding with one Memory-section override and the artifact loop parked.</summary>
    private static Dictionary<string, string?> MemorySetting(string key, string value) =>
        new(MemoryTestKit.WorkloadBinding)
        {
            ["Storage:Gc:IntervalSeconds"] = "3600",
            [$"Memory:{key}"] = value,
        };
}
