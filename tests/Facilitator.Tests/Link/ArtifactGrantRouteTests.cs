using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;
using Facilitator.Tests.Artifacts;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Link;

/// <summary>
/// Delivery-scoped read grants on the workload content route: a router-team
/// principal holding an active grant reads a foreign-team artifact ONCE (rent
/// exactly once) — the read itself consumes with a two-line audit trail; a
/// second read, an expired grant, or a plain foreign principal all answer 404.
/// A rented principal may never WRITE (PUT stays strict), and a receipt revokes
/// a never-read rental (replay answers with the stored receipt: zero new rows).
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactGrantRouteTests(PostgresFixture fixture)
{
    private static readonly byte[] Bytes = "granted bytes, rented once\n"u8.ToArray();

    private static IReadOnlyDictionary<string, string?> RouterRoster => new Dictionary<string, string?>
    {
        ["Link:Teams:2"] = "router",
    };

    private static string DigestOf(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A stored artifact owned by <c>team-alpha</c>; returns its id.</summary>
    private static async Task<Guid> StoredAlphaArtifactAsync(KernelTestFactory factory, HttpClient kernel, string tag)
    {
        var evidence = "oidc:" + await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");
        var alpha = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"{tag}-alpha", team: "team-alpha"));
        var ack = await KernelOps.PostJsonAsync(alpha, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope("cmd-grant-" + tag, Bytes, evidence), HttpStatusCode.Accepted);
        var id = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        var put = await alpha.PutAsync($"/api/link/v1/artifacts/{id}/content", new ByteArrayContent(Bytes));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return id;
    }

    private static async Task MintGrantAsync(
        KernelTestFactory factory, Guid artifactId, string endpoint, string commandId, TimeSpan? life = null)
    {
        using var scope = factory.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<ArtifactGrantStore>();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync();
        var now = DateTime.UtcNow;
        grants.Mint(db, artifactId, "router", endpoint, commandId, "kernel:reply", "reply-media", now);
        await db.SaveChangesAsync();
        if (life is { } ttl)
        {
            await db.ArtifactGrants.Where(g => g.CommandId == commandId && g.ArtifactId == artifactId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, now.Add(ttl)));
        }
    }

    [Fact]
    public async Task GrantRentsOneReadConsumesOnReadAuditsTwiceAndRejectsWrite()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var tag = "gr" + Guid.NewGuid().ToString("N")[..8];
        var artifactId = await StoredAlphaArtifactAsync(factory, kernel, tag);
        var router = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"{tag}-router", team: "router"));
        await MintGrantAsync(factory, artifactId, $"{tag}-router", "communication-reply:" + tag);

        var path = $"/api/link/v1/artifacts/{artifactId}/content";

        // First read: rented — the exact bytes with the digest ETag.
        var served = await router.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal($"\"{DigestOf(Bytes)}\"", served.Headers.ETag?.Tag);
        Assert.Equal(Bytes, await served.Content.ReadAsByteArrayAsync());

        // Rent exactly once: the second read is a plain foreign-team 404.
        var second = await router.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        // The route guards stay strict: a rented principal may not write.
        var put = await router.PutAsync(path, new ByteArrayContent(Bytes));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync();
        var grant = await db.ArtifactGrants.SingleAsync(g => g.ArtifactId == artifactId && g.CommandId == "communication-reply:" + tag);
        Assert.Equal("consumed", grant.Status);
        Assert.Equal("read", grant.ConsumeReason);
        Assert.NotNull(grant.ConsumedAt);

        // One read = exactly two audit lines (read + consume), correlated to the grant.
        var audit = await db.Audit.Where(a => a.CorrelationId == grant.GrantId.ToString()).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Action == "artifact.grant.read" && a.PolicyVersion == "artifact-v1");
        Assert.Contains(audit, a => a.Action == "artifact.grant.consume" && a.Reason == "read");
        using var detail = JsonDocument.Parse(audit.First(a => a.Action == "artifact.grant.read").Detail);
        Assert.Equal(artifactId.ToString(), detail.RootElement.GetProperty("artifactId").GetString());
        Assert.Equal("communication-reply:" + tag, detail.RootElement.GetProperty("commandId").GetString());
    }

    [Fact]
    public async Task ExpiredGrantAnswersNotFoundAndStampsExpiry()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var tag = "gx" + Guid.NewGuid().ToString("N")[..8];
        var artifactId = await StoredAlphaArtifactAsync(factory, kernel, tag);
        var routerClient = await LinkTestHarness.EnrollAsync(kernel, $"{tag}-router", team: "router");
        var router = factory.CreateLinkClient(routerClient);
        await MintGrantAsync(factory, artifactId, $"{tag}-router", "communication-reply:" + tag, life: TimeSpan.FromMinutes(-1));

        var served = await router.GetAsync($"/api/link/v1/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.NotFound, served.StatusCode);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync();
        var grant = await db.ArtifactGrants.SingleAsync(g => g.ArtifactId == artifactId && g.CommandId == "communication-reply:" + tag);
        Assert.Equal("expired", grant.Status);
        Assert.Equal("expiry", grant.ConsumeReason);
        Assert.NotNull(grant.ConsumedAt);
        // Lazy expiry mints no audit noise: only a consumed read writes lines.
        Assert.Empty(await db.Audit.Where(a => a.CorrelationId == grant.GrantId.ToString()).ToListAsync());
    }

    [Fact]
    public async Task ReceiptRevokesNeverReadGrantAndReplayWritesNothing()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var tag = "grc" + Guid.NewGuid().ToString("N")[..8];
        var artifactId = await StoredAlphaArtifactAsync(factory, kernel, tag);
        await MintGrantAsync(factory, artifactId, $"{tag}-router", "communication-reply:" + tag);

        using var scope = factory.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<ArtifactGrantStore>();
        var revoked = await grants.RevokeByCommandAsync("communication-reply:" + tag, $"{tag}-router", CancellationToken.None);
        Assert.Equal(1, revoked);

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            var grant = await db.ArtifactGrants.SingleAsync(g => g.ArtifactId == artifactId);
            Assert.Equal("revoked", grant.Status);
            Assert.Equal("receipt", grant.ConsumeReason);
            var audit = await db.Audit.Where(a => a.CorrelationId == grant.GrantId.ToString()).ToListAsync();
            Assert.Single(audit);
            Assert.Equal("artifact.grant.consume", audit[0].Action);
            Assert.Equal("receipt", audit[0].Reason);
        }

        // Replay: zero rows, zero audit (the stored-receipt posture).
        var again = await grants.RevokeByCommandAsync("communication-reply:" + tag, $"{tag}-router", CancellationToken.None);
        Assert.Equal(0, again);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            var grant = await db.ArtifactGrants.SingleAsync(g => g.ArtifactId == artifactId);
            Assert.Equal(1, await db.Audit.CountAsync(a => a.CorrelationId == grant.GrantId.ToString()));
        }
    }

    [Fact]
    public async Task ForeignRouterWithoutGrantGetsPlain404()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var tag = "gn" + Guid.NewGuid().ToString("N")[..8];
        var artifactId = await StoredAlphaArtifactAsync(factory, kernel, tag);
        var router = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"{tag}-router", team: "router"));
        var gone = await router.GetAsync($"/api/link/v1/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
