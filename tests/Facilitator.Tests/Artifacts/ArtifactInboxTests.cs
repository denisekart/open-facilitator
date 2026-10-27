using System.Net;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Inbox idempotence for the upload command: a re-POSTed envelope answers the
/// duplicate ack, allocates nothing new, and still carries the live ticket so
/// the retrying producer can complete its upload.
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactInboxTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DuplicateRequestUploadReattachesTicketSingleRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

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
        Assert.Equal(1, await fixture.Db.ArtifactCountForCommand(commandId));

        // ...and the upload still works through the re-attached location.
        var put = await ArtifactTestKit.PutBytesAsync(link, data.GetProperty("uploadLocation").GetString()!, ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("stored", (await fixture.Db.Artifact(artifactId))!.State);

        // A third touch after the slot was spent: duplicate WITHOUT data.
        var third = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", third.GetProperty("status").GetString());
        Assert.False(third.TryGetProperty("data", out var d) && d.ValueKind != System.Text.Json.JsonValueKind.Null);
    }
}
