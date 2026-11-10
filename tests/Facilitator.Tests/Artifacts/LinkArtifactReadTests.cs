using System.Net;
using System.Security.Cryptography;



namespace Facilitator.Tests.Artifacts;

/// <summary>
/// The link-plane content READ (<c>GET /api/link/v1/artifacts/{id}/content</c>):
/// the guard is the content PUT's exact pair — link token + row team ownership
/// in the ledger. The allocating team reads its own uploaded bytes back
/// verbatim with the declared content type; a foreign-team workload and a
/// not-yet-stored (pending) allocation answer 404 (non-disclosure), so an
/// assign's <c>sourceRefs</c> are dereferenceable by the harness that owns the
/// Work and by nobody else.
/// </summary>
[Collection("postgres:work")]
public sealed class LinkArtifactReadTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LinkChildReadsBackOwnUploadAndForeignTeamGets404()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerEndpoint = $"art-read-{suffix}";
        var foreignEndpoint = $"art-read-x-{suffix}";
        var owner = factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(kernel, ownerEndpoint));
        var foreign = factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(kernel, foreignEndpoint, team: "team-beta"));

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(owner, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                commandId, ArtifactTestKit.ReportBytes, await ArtifactTestKit.AliceEvidenceAsync()),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        var path = $"/api/link/v1/artifacts/{artifactId}/content";

        // Pending allocation: the row exists, the bytes do not — 404, not a lie.
        var early = await owner.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);

        var put = await ArtifactTestKit.PutBytesAsync(owner, path, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Owning workload reads the exact bytes with the declared media type.
        var served = await owner.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("text/markdown", served.Content.Headers.ContentType!.MediaType);
        var fetched = await served.Content.ReadAsByteArrayAsync();
        Assert.Equal(ArtifactTestKit.ReportBytes, fetched);
        Assert.Equal(
            ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes),
            "sha256:" + Convert.ToHexString(SHA256.HashData(fetched)).ToLowerInvariant());

        // Foreign team: the row is not theirs — 404 non-disclosure.
        var denied = await foreign.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }
}
