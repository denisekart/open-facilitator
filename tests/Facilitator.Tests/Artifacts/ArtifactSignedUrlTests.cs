using System.Net;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Storage;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// The ticket's direct-upload face: on an object-store backend the ticket
/// carries a presigned PUT for the DIGEST-DERIVED key the admission fixed
/// (never a caller-chosen address), bytes bypass the kernel, and
/// <c>POST .../confirm</c> settles the row through the kernel's own re-hash
/// law (ETags are never trusted). On the filesystem leg the same ticket
/// surface degrades honestly: no <c>uploadUrl</c>, <c>method: "POST"</c>, and
/// a confirm request answers 409 <c>artifact-not-confirmable</c> — the row
/// stays pending for the proxied PUT.
/// </summary>
public abstract class ArtifactSignedUrlTestsBase
{
    protected readonly PostgresFixture _fixture;

    protected ArtifactSignedUrlTestsBase(PostgresFixture fixture) => _fixture = fixture;

    protected virtual IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.WorkloadBinding;
    /// <summary>The mode-(b) endpoint id this leg enrolls: read from the leg's
    /// own binding settings, so both matrix legs never share an endpoint id
    /// (a re-enroll of the same id fences the other leg's live token).</summary>
    protected virtual string WorkloadEndpoint =>
        ProviderSettings["Link:WorkloadIdentities:2:EndpointId"]!;


    /// <summary>The StorageUri scheme prefix a kernel-stored row must carry on this leg.</summary>
    protected abstract string ExpectedScheme { get; }

    /// <summary>The ticket face every leg must carry (method + uploadUrl shape differ by provider).</summary>
    protected abstract string ExpectedMethod { get; }

    [Fact]
    public async Task AllocationCarriesTheProviderTicketFace()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var created = await KernelOps.PostJsonAsync(alice, "/api/artifacts/request-upload",
            JsonSerializer.Serialize(new
            {
                mediaType = "text/plain",
                size = ArtifactTestKit.ReportBytes.LongLength,
                digest = ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes),
            }),
            HttpStatusCode.Created);

        Assert.Equal(ExpectedMethod, created.GetProperty("method").GetString());
        if (ExpectedMethod == "PUT")
        {
            var url = created.GetProperty("uploadUrl").GetString()!;
            Assert.StartsWith("http", url);
            // D2: the URL addresses the digest-derived key, not a caller-chosen one.
            var hex = ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes)["sha256:".Length..];
            Assert.Contains($"{hex[..2]}/{hex}", url);
        }
        else
        {
            Assert.False(created.TryGetProperty("uploadUrl", out _));
        }

        // The proxied path stays valid on BOTH legs: uploadLocation still stores.
        var artifactId = Guid.Parse(created.GetProperty("artifactId").GetString()!);
        var put = await ArtifactTestKit.PutBytesAsync(alice, created.GetProperty("uploadLocation").GetString()!, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("stored", row!.State);
        Assert.StartsWith(ExpectedScheme, row.StorageUri);
    }

    [Fact]
    public async Task ConfirmBeforeAnyBytesRejectsToFreshAllocation()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var created = await KernelOps.PostJsonAsync(alice, "/api/artifacts/request-upload",
            JsonSerializer.Serialize(new
            {
                mediaType = "text/plain",
                size = ArtifactTestKit.ReportBytes.LongLength,
                digest = ArtifactTestKit.FreshDigest(),
            }),
            HttpStatusCode.Created);
        var artifactId = Guid.Parse(created.GetProperty("artifactId").GetString()!);

        var confirm = await alice.PostAsync($"/api/artifacts/{artifactId}/confirm", new StringContent("{}", null, "application/json"));
        if (ExpectedMethod == "PUT")
        {
            // No object at the digest key yet: the slot is still awaiting upload.
            Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        }
        else
        {
            // Filesystem: direct upload cannot exist, so confirm refuses by law.
            Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        }

        Assert.Equal("pending", (await _fixture.Db.Artifact(artifactId))!.State);
    }
}

/// <summary>Filesystem leg: no URLs, confirm answers 409.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactSignedUrlTests(PostgresFixture fixture) : ArtifactSignedUrlTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "kernel-fs:";

    /// <inheritdoc />
    protected override string ExpectedMethod => "POST";
}

/// <summary>
/// s3 leg: the full direct-upload vertical plus the SigV4 expiry law, tested
/// against the shared SeaweedFS container with a plain HttpClient (proving
/// the URL alone authorizes the PUT — no kernel, no link token on the byte
/// hop).
/// </summary>
[Collection("postgres:work:s3")]
public sealed class S3ArtifactSignedUrlTests(PostgresFixture fixture) : ArtifactSignedUrlTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "s3:";

    /// <inheritdoc />
    protected override string ExpectedMethod => "PUT";

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;

    [Fact]
    public async Task DirectUploadConfirmVerticalStoresWithoutKernelBytes()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var token = await ArtifactTestKit.EnrollModeBAsync(kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret");
        var link = factory.CreateLinkClient(token);

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                commandId, ArtifactTestKit.ReportBytes, await ArtifactTestKit.AliceEvidenceAsync(), workId: null),
            HttpStatusCode.Accepted);
        var data = ack.GetProperty("data");
        var artifactId = Guid.Parse(data.GetProperty("artifactId").GetString()!);
        Assert.Equal("PUT", data.GetProperty("method").GetString());
        var uploadUrl = data.GetProperty("uploadUrl").GetString()!;

        // Bytes go straight at the store from a bare client — no auth header,
        // the presigned URL IS the authorization.
        using var bare = new HttpClient();
        var put = await bare.PutAsync(uploadUrl, new ByteArrayContent(ArtifactTestKit.ReportBytes));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("pending", (await _fixture.Db.Artifact(artifactId))!.State);

        // Confirm on the link plane: kernel HEADs + re-hashes, then settles.
        var confirm = await KernelOps.PostJsonAsync(link, $"/api/link/v1/artifacts/{artifactId}/confirm", "{}", HttpStatusCode.OK);
        Assert.Equal("stored", confirm.GetProperty("state").GetString());
        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("verified", row!.Verification);
        Assert.Equal(ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes), row.Digest);
        Assert.StartsWith("s3:", row.StorageUri);

        // Retrieval: the content route 302s to a presigned GET; the client
        // follows and gets exactly the declared bytes.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var served = await ArtifactTestKit.GetContentAsync(alice, $"/api/artifacts/{artifactId}/content");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        // The signed response-content-type override carries the ROW's media
        // type even though the direct PUT landed the blob without one.
        Assert.Equal("text/markdown", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ArtifactTestKit.ReportBytes, await served.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WrongBytesAtDeclaredKeyRejectSlotOnConfirm()
    {
        await AssertConfirmRejects(Encoding.UTF8.GetBytes("# tampered report\nkernel-boundary digest law proved here\n"));
    }

    [Fact]
    public async Task ShortObjectAtDeclaredKeyRejectsOnSizeLaw()
    {
        await AssertConfirmRejects(Encoding.UTF8.GetBytes("# short"));
    }

    [Fact]
    public async Task ExpiredPresignedUrlIsRefusedByTheStore()
    {
        var store = new S3ArtifactStore(new ArtifactOptions
        {
            Provider = "s3",
            S3 =
            {
                Endpoint = SeaweedFsFixture.Endpoint,
                Bucket = SeaweedFsFixture.Bucket,
                Region = "us-east-1",
                AccessKey = SeaweedFsFixture.AccessKey,
                SecretKey = SeaweedFsFixture.SecretKey,
            },
        });
        var hex = ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes)["sha256:".Length..];
        var key = $"{hex[..2]}/{hex}";
        var url = await store.PresignUploadAsync(key, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(url);
        await Task.Delay(2500);
        using var bare = new HttpClient();
        var put = await bare.PutAsync(url!, new ByteArrayContent(ArtifactTestKit.ReportBytes));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        store.Dispose();
    }

    private async Task AssertConfirmRejects(byte[] bytes)
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var created = await KernelOps.PostJsonAsync(alice, "/api/artifacts/request-upload",
            JsonSerializer.Serialize(new
            {
                mediaType = "text/plain",
                size = ArtifactTestKit.ReportBytes.LongLength,
                digest = ArtifactTestKit.FreshDigest(),
            }),
            HttpStatusCode.Created);
        var artifactId = Guid.Parse(created.GetProperty("artifactId").GetString()!);

        using var bare = new HttpClient();
        var put = await bare.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var confirm = await alice.PostAsync($"/api/artifacts/{artifactId}/confirm", new StringContent("{}", null, "application/json"));
        // Either rejection law (digest-mismatch or size-mismatch) answers 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("rejected", row!.State);
    }
}
