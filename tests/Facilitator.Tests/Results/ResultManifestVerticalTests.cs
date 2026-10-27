using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;
using Facilitator.Tests.Artifacts;

namespace Facilitator.Tests.Results;

/// <summary>
/// The Plan 15 Step 2 vertical on the filesystem leg: a worker uploads a
/// canonical manifest document (link request_upload ⇒ ack ticket ⇒ PUT), posts
/// a completed event carrying <c>resultManifestRef</c> + <c>resultDigest</c>,
/// and the kernel records the manifest into the ledger — digest recomputed from
/// the STORED bytes, chain-law revision supersession, output cross-checks,
/// non-disclosure 404s, and the legacy inline-only path untouched.
/// </summary>
[Collection("postgres:work")]
public sealed class ResultManifestVerticalTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task CompletedWithManifestRefRecordsChainHead()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        var doc = ResultManifestTestKit.ManifestDoc(
            "man-v1",
            structuredResult: new JsonObject { ["score"] = 1 });
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(doc);
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes);

        var assignmentId = Guid.NewGuid();
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-v1", artifactId, digest),
            HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());

        var row = Assert.Single(await _fixture.Db.ManifestRowsForAssignment(assignmentId));
        Assert.Equal("man-v1", row.ManifestId);
        Assert.Equal(workId.ToString(), row.WorkId);
        Assert.Equal(artifactId.ToString(), row.ArtifactId);
        Assert.Equal(1, row.Revision);
        Assert.Equal(string.Empty, row.RevisionOf);
        Assert.Equal(digest, row.Digest);
        Assert.Equal("available", row.ResultState);
        Assert.Equal("recorded", row.State);
        using var structured = JsonDocument.Parse("""{"score":1}""");
        Assert.Equal(ApprovalDigest.CanonicalDocument(structured.RootElement), row.StructuredDigest);

        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "manifest.recorded"));
        Assert.Contains(
            await _fixture.Db.AuditRows(),
            a => a.Action == "manifest.recorded" && a.Resource == $"manifest/man-v1" && a.Decision == "allow");

        // Pull surface: the owning team reads the chain, newest first.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var listed = await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/manifests", HttpStatusCode.OK);
        var first = Assert.Single(listed.EnumerateArray());
        Assert.Equal("man-v1", first.GetProperty("manifestId").GetString());
        Assert.Equal(digest, first.GetProperty("digest").GetString());
        Assert.Equal(1, first.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ReplayAndSameArtifactRedeliveryStaySingleRow()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        var doc = ResultManifestTestKit.ManifestDoc("man-replay");
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(doc);
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes);
        var assignmentId = Guid.NewGuid();
        var envelope = ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-replay", artifactId, digest);

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);

        // Exact envelope replay: inbox duplicate, one row.
        var replay = await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());
        Assert.Single(await _fixture.Db.ManifestRowsForAssignment(assignmentId));

        // Same manifest artifact + digest under a fresh envelope: the ledger's
        // head-equality rule answers Duplicate (accepted, no second row).
        var reannounce = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-replay", artifactId, digest),
            HttpStatusCode.Accepted);
        Assert.Equal("accepted", reannounce.GetProperty("status").GetString());
        Assert.Single(await _fixture.Db.ManifestRowsForAssignment(assignmentId));
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "manifest.recorded"));
    }

    [Fact]
    public async Task TamperedDigestAndInlineMismatchRefuse422WithoutRows()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        var doc = ResultManifestTestKit.ManifestDoc("man-tamper");
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(doc);
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes);

        // Leg 1: declared digest tampered (valid form, wrong value).
        var a1 = Guid.NewGuid();
        var tampered = digest[..^1] == "0" ? digest[..^1] + "1" : digest[..^1] + "0";
        var r1 = await link.PostAsync("/api/link/v1/events",
            new StringContent(ResultManifestTestKit.CompletedEnvelope(workId, a1, "man-tamper", artifactId, tampered), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r1.StatusCode);
        Assert.Contains("manifest-digest-mismatch", ResultManifestTestKit.ProblemType(r1));

        // Leg 2: envelope's inline manifestId contradicts the stored document.
        var a2 = Guid.NewGuid();
        var envelope = JsonNode.Parse(ResultManifestTestKit.CompletedEnvelope(workId, a2, "man-tamper", artifactId, digest))!;
        envelope["data"]!["resultManifest"]!.AsObject()["manifestId"] = "man-liar";
        var r2 = await link.PostAsync("/api/link/v1/events",
            new StringContent(envelope.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r2.StatusCode);
        Assert.Contains("manifest-digest-mismatch", ResultManifestTestKit.ProblemType(r2));

        Assert.Empty(await _fixture.Db.ManifestRowsForAssignment(a1));
        Assert.Empty(await _fixture.Db.ManifestRowsForAssignment(a2));
        Assert.Equal(0, await _fixture.Db.TimelineEventCount(workId, "manifest.recorded"));
    }

    [Fact]
    public async Task CorrectionSupersedesHeadWithChainProofs()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);
        var assignmentId = Guid.NewGuid();

        var doc1 = ResultManifestTestKit.ManifestDoc("man-c1");
        var (bytes1, digest1) = ResultManifestTestKit.ManifestBytes(doc1);
        var artifact1 = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes1);
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-c1", artifact1, digest1),
            HttpStatusCode.Accepted);

        var doc2 = ResultManifestTestKit.ManifestDoc("man-c2", revisionOf: "man-c1");
        var (bytes2, digest2) = ResultManifestTestKit.ManifestBytes(doc2);
        var artifact2 = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes2);
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-c2", artifact2, digest2),
            HttpStatusCode.Accepted);

        var rows = await _fixture.Db.ManifestRowsForAssignment(assignmentId);
        Assert.Equal(2, rows.Count);
        Assert.Equal("superseded", Assert.Single(rows, r => r.ManifestId == "man-c1").State);
        Assert.Equal("superseded", Assert.Single(rows, r => r.ManifestId == "man-c1").ResultState);
        var head = Assert.Single(rows, r => r.ManifestId == "man-c2");
        Assert.Equal(2, head.Revision);
        Assert.Equal("man-c1", head.RevisionOf);
        Assert.Equal("recorded", head.State);
        Assert.Equal(digest2, head.Digest);

        Assert.Equal(2, await _fixture.Db.TimelineEventCount(workId, "manifest.recorded"));
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "manifest.superseded"));

        // Newest-revision-first ordering on the pull surface.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var listed = await KernelOps.GetJsonAsync(alice, $"/api/works/{workId}/manifests", HttpStatusCode.OK);
        Assert.Equal(2, listed.GetArrayLength());
        Assert.Equal("man-c2", listed[0].GetProperty("manifestId").GetString());
    }

    [Fact]
    public async Task ForeignTeamManifestAnswers404()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        // Work on team-beta; the manifest uploads under the team-alpha workload.
        var workId = await TeamWorkAsync(kernel, "team-beta");
        var link = await LinkClientAsync(factory, kernel);

        var doc = ResultManifestTestKit.ManifestDoc("man-foreign");
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(doc);
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes);

        var assignmentId = Guid.NewGuid();
        var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-foreign", artifactId, digest), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("manifest-not-found", ResultManifestTestKit.ProblemType(response));
        Assert.Empty(await _fixture.Db.ManifestRowsForAssignment(assignmentId));
    }

    [Fact]
    public async Task LegacyInlineOnlyCompletedStillAcceptedNoLedgerEffect()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.LegacyCompletedEnvelope(), HttpStatusCode.Accepted);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());
        Assert.Equal(0, await _fixture.Db.TimelineEventCount(workId, "manifest.recorded"));
    }

    [Fact]
    public async Task UnresolvableOutputsRefuse422WithoutRows()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        // A real, verified, same-team artifact to declare (wrong digest below).
        var outputId = await ResultManifestTestKit.UploadManifestAsync(link, workId, ArtifactTestKit.ReportBytes);
        var outputDigest = (await _fixture.Db.Artifact(outputId))!.Digest;

        // Leg 1: declared digest contradicts the stored row.
        var wrong = new JsonObject { ["artifactId"] = outputId.ToString(), ["mediaType"] = "application/octet-stream", ["size"] = ArtifactTestKit.ReportBytes.LongLength, ["digest"] = ArtifactTestKit.FreshDigest(), ["storageUrl"] = "external:blob/x", ["createdBy"] = "plan15", ["createdAt"] = "2026-10-27T00:00:00Z" };
        var doc1 = ResultManifestTestKit.ManifestDoc("man-out1", outputs: new JsonArray(wrong));
        var (bytes1, digest1) = ResultManifestTestKit.ManifestBytes(doc1);
        var artifact1 = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes1);
        var a1 = Guid.NewGuid();
        var r1 = await link.PostAsync("/api/link/v1/events",
            new StringContent(ResultManifestTestKit.CompletedEnvelope(workId, a1, "man-out1", artifact1, digest1), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r1.StatusCode);
        Assert.Contains("manifest-output-unresolvable", ResultManifestTestKit.ProblemType(r1));
        Assert.Empty(await _fixture.Db.ManifestRowsForAssignment(a1));

        // Leg 2: resolvable digest but an unknown (never-allocated) artifact id.
        var unknown = JsonNode.Parse(wrong.ToJsonString())!.AsObject();
        unknown["artifactId"] = Guid.NewGuid().ToString();
        unknown["digest"] = outputDigest;
        var doc2 = ResultManifestTestKit.ManifestDoc("man-out2", outputs: new JsonArray(unknown));
        var (bytes2, digest2) = ResultManifestTestKit.ManifestBytes(doc2);
        var artifact2 = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes2);
        var a2 = Guid.NewGuid();
        var r2 = await link.PostAsync("/api/link/v1/events",
            new StringContent(ResultManifestTestKit.CompletedEnvelope(workId, a2, "man-out2", artifact2, digest2), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r2.StatusCode);
        Assert.Empty(await _fixture.Db.ManifestRowsForAssignment(a2));
    }

    [Fact]
    public async Task ResolvedOutputsRecordAndPinThroughTheRow()
    {
        using var factory = Host();
        var kernel = factory.CreateAuthedClient();
        var workId = await TeamWorkAsync(kernel, "team-alpha");
        var link = await LinkClientAsync(factory, kernel);

        var outputId = await ResultManifestTestKit.UploadManifestAsync(link, workId, ArtifactTestKit.ReportBytes);
        var outputDigest = (await _fixture.Db.Artifact(outputId))!.Digest;
        var doc = ResultManifestTestKit.ManifestDoc(
            "man-pinned", outputs: new JsonArray(ResultManifestTestKit.Output(outputId, outputDigest, ArtifactTestKit.ReportBytes.LongLength)));
        var (bytes, digest) = ResultManifestTestKit.ManifestBytes(doc);
        var artifactId = await ResultManifestTestKit.UploadManifestAsync(link, workId, bytes);

        var assignmentId = Guid.NewGuid();
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ResultManifestTestKit.CompletedEnvelope(workId, assignmentId, "man-pinned", artifactId, digest),
            HttpStatusCode.Accepted);
        Assert.Single(await _fixture.Db.ManifestRowsForAssignment(assignmentId));
    }

    private KernelTestFactory Host() =>
        new(_fixture, noDispatcher: true, settings: ResultManifestTestKit.WorkloadBinding);

    private async Task<Guid> TeamWorkAsync(HttpClient kernel, string team)
    {
        var projectId = await _fixture.Db.SeedProject(team);
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, $"manifest vertical ({team})", projectId);
        return workId;
    }

    private static async Task<HttpClient> LinkClientAsync(KernelTestFactory factory, HttpClient kernel) =>
        factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(kernel, "man.wk1-fs", "ofac-gateway-alpha", "gateway-alpha-secret"));

    private static JsonElement ParseEl(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
