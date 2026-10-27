using System.Net;
using System.Text;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// External registration law: an artifact.available carrying a non-kernel
/// artifactId registers a row (`stored`/`unverified`, `external:` URL, the
/// kernel NEVER fetches bytes); a kernel-allocated id confirms the row without
/// rewriting it; a contradicting re-declaration denies 422 + audit.
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactExternalRegistrationTests(PostgresFixture fixture)
{
    private const string ForeignDigest = "sha256:abababababababababababababababababababababababababababababababab";

    [Fact]
    public async Task ExternalDeclarationRegistersWithoutBytes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, "art.ext1"));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "external register");

        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/x.md", workId),
            HttpStatusCode.Accepted);

        var row = Assert.Single(await fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("stored", row.State);
        Assert.Equal("unverified", row.Verification);
        Assert.Equal("external", row.Origin);
        Assert.StartsWith("external:https://example.invalid/x.md", row.StorageUri);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "artifact.registered"));

        // Kernel never fetched: the blob surface answers absent for the row.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/artifacts/{row.ArtifactId}/content")).StatusCode);
    }

    [Fact]
    public async Task RedeliveredEnvelopeIsInboxDuplicateSameWireIdDedupes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, "art.ext2"));
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        var envelope = ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/y.md");

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);
        // Same envelope re-POSTed: inbox dedupe is envelope-id-scoped.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Single(await fixture.Db.ArtifactByWireId(wireId));

        // A NEW envelope re-declaring the same wire id + digest: still one row.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/y.md"),
            HttpStatusCode.Accepted);
        Assert.Single(await fixture.Db.ArtifactByWireId(wireId));
    }

    [Fact]
    public async Task KernelStoredConfirmationLeavesRowUntouchedAndMismatchDenies()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var plain = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, "art.ext3"));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "confirm path");
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));

        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid()), ArtifactTestKit.ReportBytes,
                "identity-evidence:opaque", workId),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        await ArtifactTestKit.PutBytesAsync(link, ack.GetProperty("data").GetProperty("uploadLocation").GetString()!,
            ArtifactTestKit.ReportBytes);
        var declared = ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes);

        // Confirmation: kernel-allocated Guid, identical declaration -> note only.
        await KernelOps.PostJsonAsync(plain, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(artifactId.ToString(), declared, workId: workId,
                size: ArtifactTestKit.ReportBytes.LongLength),
            HttpStatusCode.Accepted);
        var row = await fixture.Db.Artifact(artifactId);
        Assert.Equal("stored", row!.State);
        Assert.Equal("verified", row.Verification);
        Assert.StartsWith("kernel-fs:", row.StorageUri);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "artifact.available"));

        // Contradicting re-declaration of the same kernel id: refused + deny audit.
        var clash = await plain.PostAsync("/api/link/v1/events",
            new StringContent(ArtifactTestKit.AvailableEnvelope(artifactId.ToString(), ForeignDigest,
                workId: workId, size: 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/artifact-declaration-mismatch",
            ArtifactTestKit.ProblemType(clash));
        Assert.Contains(await fixture.Db.AuditRows(decision: "deny"),
            a => a.Action == "artifact.reregistration-denied"
                 && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));
        Assert.Equal("verified", (await fixture.Db.Artifact(artifactId))!.Verification);
    }

    [Fact]
    public async Task CloudEventPlaneRegistersAndConfirmsIdentically()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ArtifactTestKit.WorkloadBinding);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "ce plane artifacts");

        // External form over /api/ingress/events behaves like the link arm.
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/z.md", workId),
            HttpStatusCode.Accepted);
        var row = Assert.Single(await fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("external", row.Origin);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "artifact.registered"));

        // Confirmation form: a kernel-stored Guid's row is untouched, noted once.
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, "art.wk1", "ofac-gateway-alpha", "gateway-alpha-secret"));
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid()), ArtifactTestKit.ReportBytes,
                "identity-evidence:opaque", workId),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
        await ArtifactTestKit.PutBytesAsync(link, ack.GetProperty("data").GetProperty("uploadLocation").GetString()!,
            ArtifactTestKit.ReportBytes);

        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            ArtifactTestKit.AvailableEnvelope(artifactId.ToString(),
                ArtifactTestKit.DigestOf(ArtifactTestKit.ReportBytes), workId: workId,
                size: ArtifactTestKit.ReportBytes.LongLength),
            HttpStatusCode.Accepted);
        Assert.Equal("verified", (await fixture.Db.Artifact(artifactId))!.Verification);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "artifact.available"));
    }
}
