using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// The vertical: Link request_upload (delegated evidence, mode-(b) workload)
/// ⇒ ticket in the ack `data` ⇒ PUT bytes ⇒ digest verified, row stored,
/// timeline entry, content readable by the owning team. Provider-matrixed:
/// the fs leg proxies bytes through the kernel; the s3 leg uploads the same
/// way (link-plane proxied PUT) — the presigned direct path is the
/// ArtifactSignedUrlTests surface.
/// </summary>
public abstract class ArtifactUploadVerticalTestsBase
{
    private readonly PostgresFixture _fixture;

    protected ArtifactUploadVerticalTestsBase(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Factory settings for this provider leg.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.WorkloadBinding;
    /// <summary>The mode-(b) endpoint id this leg enrolls: read from the leg's
    /// own binding settings, so both matrix legs never share an endpoint id
    /// (a re-enroll of the same id fences the other leg's live token).</summary>
    protected virtual string WorkloadEndpoint =>
        ProviderSettings["Link:WorkloadIdentities:2:EndpointId"]!;


    /// <summary>The StorageUri scheme prefix a kernel-stored row must carry on this leg.</summary>
    protected abstract string ExpectedScheme { get; }

    [Fact]
    public async Task LinkUploadHappyPathStoresVerifiesAndTimelines()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "artifact vertical");
        var token = await ArtifactTestKit.EnrollModeBAsync(kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret");
        var link = factory.CreateLinkClient(token);

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                commandId, ArtifactTestKit.ReportBytes, await ArtifactTestKit.AliceEvidenceAsync(), workId),
            HttpStatusCode.Accepted);

        // Ticket rides IN-BAND on the ack.
        var data = ack.GetProperty("data");
        var artifactId = Guid.Parse(data.GetProperty("artifactId").GetString()!);
        Assert.Equal($"/api/link/v1/artifacts/{artifactId}/content", data.GetProperty("uploadLocation").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("expiresAt").GetString()));

        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("pending", row!.State);
        Assert.Equal("team-alpha", row.TeamSlug);
        Assert.Equal(workId.ToString(), row.WorkId);

        // Bytes travel by PUT, never in the envelope.
        var put = await ArtifactTestKit.PutBytesAsync(link, data.GetProperty("uploadLocation").GetString()!, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("stored", row!.State);
        Assert.Equal("verified", row.Verification);
        Assert.Equal(ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes), row.Digest);
        Assert.StartsWith(ExpectedScheme, row.StorageUri);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.stored"));

        // Owning team reads the bytes back; digest of served bytes == declared.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var served = await ArtifactTestKit.GetContentAsync(alice, $"/api/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("text/markdown", served.Content.Headers.ContentType!.MediaType);
        var fetched = await served.Content.ReadAsByteArrayAsync();
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(fetched)).ToLowerInvariant(), row.Digest);

        // Metadata surface works and never leaks the storage URI.
        var meta = await KernelOps.GetJsonAsync(alice, $"/api/artifacts/{artifactId}", HttpStatusCode.OK);
        Assert.Equal("stored", meta.GetProperty("state").GetString());
        Assert.False(meta.TryGetProperty("storageUri", out _));

        // Timeline shows the entry on the work.
        var timeline = await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/timeline", HttpStatusCode.OK);
        Assert.Contains(timeline.EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "artifact.stored");
    }

    [Fact]
    public async Task RestTwinAllocatesAndStoresForHumanPlane()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");

        var body = JsonSerializer.Serialize(new
        {
            mediaType = "text/plain",
            size = ArtifactTestKit.ReportBytes.LongLength,
            digest = ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes),
            comment = "rest twin",
        });
        var created = await KernelOps.PostJsonAsync(alice, "/api/artifacts/request-upload", body, HttpStatusCode.Created);
        var artifactId = Guid.Parse(created.GetProperty("artifactId").GetString()!);
        Assert.Equal($"/api/artifacts/{artifactId}/content", created.GetProperty("uploadLocation").GetString());

        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("rest", row!.Origin);
        Assert.Equal("team-alpha", row.TeamSlug);

        var put = await ArtifactTestKit.PutBytesAsync(alice, $"/api/artifacts/{artifactId}/content", ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("stored", (await _fixture.Db.Artifact(artifactId))!.State);
    }
}

/// <summary>Filesystem leg of the upload vertical.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactUploadVerticalTests(PostgresFixture fixture) : ArtifactUploadVerticalTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "kernel-fs:";
}

/// <summary>s3 leg of the upload vertical (kernel-proxied PUT, s3-scheme rows).</summary>
[Collection("postgres:work:s3")]
public sealed class S3ArtifactUploadVerticalTests(PostgresFixture fixture) : ArtifactUploadVerticalTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "s3:";

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;
}
