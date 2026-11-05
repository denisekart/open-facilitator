using Facilitator.Kernel.Shared.Storage;

using Microsoft.Extensions.DependencyInjection;
namespace Facilitator.Tests.Artifacts;

/// <summary>
/// The retention-GC pass (D7/D8) on the filesystem leg with a fake clock:
/// <c>Nd</c> policies collect once lapsed and unreferenced, a live Work pins
/// the blob, <c>permanent</c> and <c>archive</c> never expire, expired
/// pending slots sweep without touching the backend, and a crash-reverted
/// row re-collects cleanly (restart-safety). Passes are driven explicitly via
/// <see cref="RetentionGcService.RunOnceAsync"/>; the factory parks the
/// background loop at one hour so no test races it. The family rides its own
/// container (<see cref="GcDbFixture"/>): a pass at an advanced fake now
/// must never tombstone a foreign collection's live pending slots.
/// </summary>
[Collection("postgres:gc")]
public sealed class ArtifactRetentionGcTests(GcDbFixture fixture)
{
    private readonly GcDbFixture _fixture = fixture;

    private const string Hex64 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Key => $"aa/{Hex64}";

    [Fact]
    public async Task ExpiredDaysPolicyCollectsAndDeletesBlob()
    {
        var (factory, gc, root) = Host();
        using (factory)
        {
            var id = Guid.NewGuid();
            var blob = BlobPath(root, Key);
            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
            await File.WriteAllTextAsync(blob, "stale bytes");
            await _fixture.Db.SeedArtifact(
                id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId: null,
                storageUri: $"kernel-fs:{Key}");

            await gc.RunOnceAsync(default);

            var row = await _fixture.Db.Artifact(id);
            Assert.Equal("deleted", row!.State);
            Assert.False(File.Exists(blob));
            Assert.Contains(
                await _fixture.Db.AuditRows(actor: "kernel"),
                a => a.Action == "artifact.gc" && a.Resource == $"artifact/{id}" && a.Reason == "retention");
        }
    }

    [Fact]
    public async Task LiveWorkPinsLapsedBlob()
    {
        var (factory, gc, _) = Host();
        using (factory)
        {
            var kernel = factory.CreateAuthedClient();
            var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "gc pin");
            var id = Guid.NewGuid();
            await _fixture.Db.SeedArtifact(
                id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId,
                storageUri: $"kernel-fs:{Key}");

            await gc.RunOnceAsync(default);

            Assert.Equal("stored", (await _fixture.Db.Artifact(id))!.State);
        }
    }

    [Fact]
    public async Task TerminalWorkReleasesLapsedBlobWithTimelineNote()
    {
        var (factory, gc, _) = Host();
        using (factory)
        {
            var kernel = factory.CreateAuthedClient();
            var (workId, version) = await KernelOps.CreateWorkAsync(kernel, "gc release");
            await KernelOps.TransitionAsync(kernel, workId, "cancelled", version);
            var id = Guid.NewGuid();
            await _fixture.Db.SeedArtifact(
                id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId,
                storageUri: $"kernel-fs:{Key}");

            await gc.RunOnceAsync(default);

            Assert.Equal("deleted", (await _fixture.Db.Artifact(id))!.State);
            Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.gc"));
        }
    }

    [Fact]
    public async Task PermanentAndArchiveNeverExpire()
    {
        var (factory, gc, _) = Host();
        using (factory)
        {
            var now = GcNow(factory).UtcDateTime;
            var permanent = Guid.NewGuid();
            var archive = Guid.NewGuid();
            await _fixture.Db.SeedArtifact(permanent, "stored", "permanent", storedAt: now.AddDays(-400), workId: null);
            await _fixture.Db.SeedArtifact(archive, "stored", "archive", storedAt: now.AddDays(-400), workId: null);

            await gc.RunOnceAsync(default);
            factory.Clock!.Advance(TimeSpan.FromDays(100));
            await gc.RunOnceAsync(default);

            Assert.Equal("stored", (await _fixture.Db.Artifact(permanent))!.State);
            Assert.Equal("stored", (await _fixture.Db.Artifact(archive))!.State);
        }
    }

    [Fact]
    public async Task ExpiredPendingSlotSweepsWithoutBackendAndIsIdempotent()
    {
        var (factory, gc, _) = Host();
        using (factory)
        {
            var id = Guid.NewGuid();
            await _fixture.Db.SeedArtifact(
                id, "pending", "permanent", storedAt: null, workId: null,
                storageUri: string.Empty,
                expiresAt: GcNow(factory).UtcDateTime.AddSeconds(-1));

            await gc.RunOnceAsync(default);

            Assert.Equal("deleted", (await _fixture.Db.Artifact(id))!.State);
            Assert.Contains(
                await _fixture.Db.AuditRows(actor: "kernel"),
                a => a.Action == "artifact.gc" && a.Resource == $"artifact/{id}" && a.Reason == "expired-slot");

            // Second pass: nothing left to collect, no throw (the slot never
            // had bytes, so no backend call was ever attempted either).
            await gc.RunOnceAsync(default);
            Assert.Equal("deleted", (await _fixture.Db.Artifact(id))!.State);
        }
    }

    [Fact]
    public async Task CrashRevertedRowRecollectsWithoutError()
    {
        var (factory, gc, root) = Host();
        using (factory)
        {
            var id = Guid.NewGuid();
            var blob = BlobPath(root, Key);
            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
            await File.WriteAllTextAsync(blob, "stale bytes");
            await _fixture.Db.SeedArtifact(
                id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId: null,
                storageUri: $"kernel-fs:{Key}");

            await gc.RunOnceAsync(default);
            File.Delete(blob); // simulate: delete landed, tombstone did not commit
            await _fixture.Db.RevertArtifactState(id, "stored");

            await gc.RunOnceAsync(default);

            Assert.Equal("deleted", (await _fixture.Db.Artifact(id))!.State);
        }
    }

    private (KernelTestFactory Factory, RetentionGcService Gc, string Root) Host()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ofac-gc-{Guid.NewGuid():N}");
        var factory = new KernelTestFactory(
            _fixture,
            fakeClock: true,
            settings: new Dictionary<string, string?> { ["Storage:FileSystem:RootPath"] = root });
        var gc = factory.Services.GetRequiredService<RetentionGcService>();
        return (factory, gc, root);
    }

    private static DateTimeOffset GcNow(KernelTestFactory factory) => factory.Clock!.GetUtcNow();

    private static string BlobPath(string root, string key) => Path.Combine(root, key);
}
