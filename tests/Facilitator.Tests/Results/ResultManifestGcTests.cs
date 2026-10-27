using Facilitator.Kernel.Shared.Storage;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Results;

/// <summary>
/// Plan 15's GC strengthening: a RECORDED result manifest pins the blob it was
/// stored under (and, via the flattened outputs column, every output artifact
/// its document declared) regardless of the Work's state — a lapsed <c>Nd</c>
/// retention policy on an unreferenced-by-Work artifact still cannot collect a
/// blob a recorded manifest vouches for; a superseded manifest releases it.
/// </summary>
[Collection("postgres:work")]
public sealed class ResultManifestGcTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private const string Hex64 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Key => $"aa/{Hex64}";

    [Fact]
    public async Task RecordedManifestPinsBlobRegardlessOfWorkState()
    {
        var (factory, gc, root) = Host();
        using (factory)
        {
            var id = Guid.NewGuid();
            var blob = Path.Combine(root, Key);
            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
            await File.WriteAllTextAsync(blob, "manifest bytes");
            // The owning Work is TERMINAL (its own reference check cannot pin);
            // only the recorded manifest can keep this blob alive.
            var workId = await _fixture.Db.SeedWork("completed");
            await _fixture.Db.SeedArtifact(
                id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId: null,
                storageUri: $"kernel-fs:{Key}");
            await _fixture.Db.SeedResultManifest("man-pin", workId, id, [], "recorded", "available");

            await gc.RunOnceAsync(default);

            Assert.Equal("stored", (await _fixture.Db.Artifact(id))!.State);
            Assert.True(File.Exists(blob));
        }
    }

    [Fact]
    public async Task OutputArtifactIdsPinBlobAndSupersededManifestReleasesIt()
    {
        var (factory, gc, root) = Host();
        using (factory)
        {
            var pinned = Guid.NewGuid();
            var free = Guid.NewGuid();
            foreach (var id in new[] { pinned, free })
            {
                var blob = Path.Combine(root, Key);
                Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                await File.WriteAllTextAsync(blob, "pinned bytes");
                await _fixture.Db.SeedArtifact(
                    id, "stored", "1d", storedAt: GcNow(factory).UtcDateTime.AddDays(-2), workId: null,
                    storageUri: $"kernel-fs:{Key}");
            }

            // One recorded manifest references `pinned` only through its
            // declared outputs; a superseded manifest references `free` and
            // must NOT pin it. The owning Work is terminal either way.
            var workId = await _fixture.Db.SeedWork("completed");
            await _fixture.Db.SeedResultManifest("man-out", workId, pinned, [pinned.ToString()], "recorded", "available");
            await _fixture.Db.SeedResultManifest("man-old", workId, free, [], "superseded", "superseded");

            await gc.RunOnceAsync(default);

            Assert.Equal("stored", (await _fixture.Db.Artifact(pinned))!.State);
            Assert.Equal("deleted", (await _fixture.Db.Artifact(free))!.State);
        }
    }

    private (KernelTestFactory Factory, RetentionGcService Gc, string Root) Host()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ofac-man-gc-{Guid.NewGuid():N}");
        var factory = new KernelTestFactory(
            _fixture,
            fakeClock: true,
            settings: new Dictionary<string, string?> { ["Storage:FileSystem:RootPath"] = root });
        var gc = factory.Services.GetRequiredService<RetentionGcService>();
        return (factory, gc, root);
    }

    private static DateTimeOffset GcNow(KernelTestFactory factory) => factory.Clock!.GetUtcNow();
}
