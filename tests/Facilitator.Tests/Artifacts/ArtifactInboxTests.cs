using System.Net;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Inbox idempotence for the upload command: a re-POSTed envelope answers the
/// duplicate ack, allocates nothing new, and still carries the live ticket so
/// the retrying producer can complete its upload. Provider-matrixed.
/// </summary>
public abstract class ArtifactInboxTestsBase
{
    private readonly PostgresFixture _fixture;

    protected ArtifactInboxTestsBase(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Factory settings for this provider leg.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.WorkloadBinding;
    /// <summary>The mode-(b) endpoint id this leg enrolls: read from the leg's
    /// own binding settings, so both matrix legs never share an endpoint id
    /// (a re-enroll of the same id fences the other leg's live token).</summary>
    protected virtual string WorkloadEndpoint =>
        ProviderSettings["Link:WorkloadIdentities:2:EndpointId"]!;


    [Fact]
    public async Task DuplicateRequestUploadReattachesTicketSingleRow()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var envelope = ArtifactTestKit.RequestUploadEnvelope(
            commandId, ArtifactTestKit.ReportBytes, "identity-evidence:opaque");
        var first = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(first.GetProperty("data").GetProperty("artifactId").GetString()!);

        var replay = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());

        // The duplicate re-carries the SAME live ticket...
        var data = replay.GetProperty("data");
        Assert.Equal(artifactId.ToString(), data.GetProperty("artifactId").GetString());
        Assert.Equal(
            first.GetProperty("data").GetProperty("uploadLocation").GetString(),
            data.GetProperty("uploadLocation").GetString());

        // ...single row...
        Assert.Equal(1, await _fixture.Db.ArtifactCountForCommand(commandId));

        // ...and the upload still works through the re-attached location.
        var put = await ArtifactTestKit.PutBytesAsync(link, data.GetProperty("uploadLocation").GetString()!, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("stored", (await _fixture.Db.Artifact(artifactId))!.State);

        // A third touch after the slot was spent: duplicate WITHOUT data.
        var third = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", third.GetProperty("status").GetString());
        Assert.False(third.TryGetProperty("data", out var d) && d.ValueKind != System.Text.Json.JsonValueKind.Null);
    }
}

/// <summary>Filesystem leg of the inbox matrix.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactInboxTests(PostgresFixture fixture) : ArtifactInboxTestsBase(fixture);

/// <summary>s3 leg of the inbox matrix.</summary>
[Collection("postgres:work:s3")]
public sealed class S3ArtifactInboxTests(PostgresFixture fixture) : ArtifactInboxTestsBase(fixture)
{
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;
}
