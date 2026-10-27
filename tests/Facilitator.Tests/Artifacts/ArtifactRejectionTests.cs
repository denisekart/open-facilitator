using System.Net;
using System.Text.Json;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Digest + size enforcement at the kernel boundary: the received bytes are
/// hashed by the kernel, a mismatch rejects the slot (fresh allocation
/// required), a deny audit records it, and the wrong-bytes blob never lands
/// as the artifact's stored content.
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactRejectionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TamperedBytesRejectSlotAndAudit()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(commandId, ArtifactTestKit.ReportBytes, "identity-evidence:opaque"),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        var location = ack.GetProperty("data").GetProperty("uploadLocation").GetString()!;

        var tampered = (byte[])ArtifactTestKit.ReportBytes.Clone();
        tampered[0] ^= 0xFF;
        var put = await ArtifactTestKit.PutBytesAsync(link, location, tampered);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/artifact-digest-mismatch",
            ArtifactTestKit.ProblemType(put));

        var row = await fixture.Db.Artifact(artifactId);
        Assert.Equal("rejected", row!.State);
        Assert.DoesNotContain(await fixture.Db.AuditRows(),
            a => a.Action == "artifact.stored" && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));
        Assert.Contains(await fixture.Db.AuditRows(),
            a => a.Action == "artifact.rejected" && a.Decision == "deny" && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));

        // The slot is spent: a re-PUT answers absent, not "retry later".
        var again = await ArtifactTestKit.PutBytesAsync(link, location, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task SizeLieRejectsSlot()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                commandId, ArtifactTestKit.ReportBytes, "identity-evidence:opaque", declaredSize: 4),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);

        var put = await ArtifactTestKit.PutBytesAsync(link,
            ack.GetProperty("data").GetProperty("uploadLocation").GetString()!, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/artifact-size-mismatch",
            ArtifactTestKit.ProblemType(put));
        Assert.Equal("rejected", (await fixture.Db.Artifact(artifactId))!.State);
    }

    [Fact]
    public async Task IdempotentRePutOfSameBytesSucceedsOnceRowStored()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(commandId, ArtifactTestKit.ReportBytes, "identity-evidence:opaque"),
            HttpStatusCode.Accepted);
        var location = ack.GetProperty("data").GetProperty("uploadLocation").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await ArtifactTestKit.PutBytesAsync(link, location, ArtifactTestKit.ReportBytes)).StatusCode);
        var second = await ArtifactTestKit.PutBytesAsync(link, location, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("stored", body.GetProperty("state").GetString());
    }
}
