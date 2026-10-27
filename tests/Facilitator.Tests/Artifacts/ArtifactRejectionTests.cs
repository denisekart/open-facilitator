using System.Net;
using System.Text.Json;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Digest + size enforcement at the kernel boundary: the received bytes are
/// hashed by the kernel, a mismatch rejects the slot (fresh allocation
/// required), a deny audit records it, and the wrong-bytes blob never lands
/// as the artifact's stored content. Provider-matrixed: the fs leg hashes on
/// the proxied stream; the s3 leg hashes the temp-key copy server-side-
/// independently — both must refuse to flip a lying declaration.
/// </summary>
public abstract class ArtifactRejectionTestsBase
{
    private readonly PostgresFixture _fixture;

    protected ArtifactRejectionTestsBase(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Factory settings for this provider leg.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.WorkloadBinding;
    /// <summary>The mode-(b) endpoint id this leg enrolls: read from the leg's
    /// own binding settings, so both matrix legs never share an endpoint id
    /// (a re-enroll of the same id fences the other leg's live token).</summary>
    protected virtual string WorkloadEndpoint =>
        ProviderSettings["Link:WorkloadIdentities:2:EndpointId"]!;


    [Fact]
    public async Task TamperedBytesRejectSlotAndAudit()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

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

        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("rejected", row!.State);
        Assert.DoesNotContain(await _fixture.Db.AuditRows(),
            a => a.Action == "artifact.stored" && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));
        Assert.Contains(await _fixture.Db.AuditRows(),
            a => a.Action == "artifact.rejected" && a.Decision == "deny" && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));

        // The slot is spent: a re-PUT answers absent, not "retry later".
        var again = await ArtifactTestKit.PutBytesAsync(link, location, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task SizeLieRejectsSlot()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

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
        Assert.Equal("rejected", (await _fixture.Db.Artifact(artifactId))!.State);
    }

    [Fact]
    public async Task IdempotentRePutOfSameBytesSucceedsOnceRowStored()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

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

/// <summary>Filesystem leg of the rejection matrix.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactRejectionTests(PostgresFixture fixture) : ArtifactRejectionTestsBase(fixture);

/// <summary>s3 leg of the rejection matrix.</summary>
[Collection("postgres:work:s3")]
public sealed class S3ArtifactRejectionTests(PostgresFixture fixture) : ArtifactRejectionTestsBase(fixture)
{
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;
}
