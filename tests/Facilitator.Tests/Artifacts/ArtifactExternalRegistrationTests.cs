using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// External registration law: an artifact.available carrying a non-kernel
/// artifactId registers a row (`stored`/`unverified`, `external:` URL, the
/// kernel NEVER fetches bytes); a kernel-allocated id confirms the row without
/// rewriting it; a contradicting re-declaration denies 422 + audit.
/// Provider-matrixed: kernel-stored confirmation must not rewrite the row's
/// provider scheme.
/// </summary>
public abstract class ArtifactExternalRegistrationTestsBase
{
    private readonly PostgresFixture _fixture;

    protected ArtifactExternalRegistrationTestsBase(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Factory settings for this provider leg.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.WorkloadBinding;
    /// <summary>The mode-(b) endpoint id this leg enrolls: read from the leg's
    /// own binding settings, so both matrix legs never share an endpoint id
    /// (a re-enroll of the same id fences the other leg's live token).</summary>
    protected string WorkloadEndpoint =>
        ProviderSettings["Link:WorkloadIdentities:2:EndpointId"]!;

    /// <summary>Collection-unambiguous suffix for free-form endpoint ids: the
    /// leg tag carried by this leg's bound workload endpoint id.</summary>
    protected string LegTag => WorkloadEndpoint[WorkloadEndpoint.LastIndexOf('-')..];

    private static string? ScreenOf(Facilitator.Tests.Infrastructure.TestDbProbe.AuditRow row) =>
        JsonNode.Parse(row.Detail)?["screen"]?.GetValue<string>();



    /// <summary>The StorageUri scheme prefix a kernel-stored row must carry on this leg.</summary>
    protected abstract string ExpectedScheme { get; }

    private const string ForeignDigest = "sha256:abababababababababababababababababababababababababababababababab";

    [Fact]
    public async Task ExternalDeclarationRegistersWithoutBytes()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"art.ext1{LegTag}"));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "external register");

        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/x.md", workId),
            HttpStatusCode.Accepted);

        var row = Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("stored", row.State);
        Assert.Equal("unverified", row.Verification);
        Assert.Equal("external", row.Origin);
        Assert.StartsWith("external:https://example.invalid/x.md", row.StorageUri);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.registered"));

        // Kernel never fetched: the blob surface answers absent for the row.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/artifacts/{row.ArtifactId}/content")).StatusCode);
    }

    [Fact]
    public async Task RedeliveredEnvelopeIsInboxDuplicateSameWireIdDedupes()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"art.ext2{LegTag}"));
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        var envelope = ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/y.md");

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.Accepted);
        // Same envelope re-POSTed: inbox dedupe is envelope-id-scoped.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events", envelope, HttpStatusCode.OK);
        Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));

        // A NEW envelope re-declaring the same wire id + digest: still one row.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/y.md"),
            HttpStatusCode.Accepted);
        Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));
    }

    [Fact]
    public async Task KernelStoredConfirmationLeavesRowUntouchedAndMismatchDenies()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var plain = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, $"art.ext3{LegTag}"));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "confirm path");
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

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
        var row = await _fixture.Db.Artifact(artifactId);
        Assert.Equal("stored", row!.State);
        Assert.Equal("verified", row.Verification);
        Assert.StartsWith(ExpectedScheme, row.StorageUri);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.available"));

        // Contradicting re-declaration of the same kernel id: refused + deny audit.
        var clash = await plain.PostAsync("/api/link/v1/events",
            new StringContent(ArtifactTestKit.AvailableEnvelope(artifactId.ToString(), ForeignDigest,
                workId: workId, size: 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/artifact-declaration-mismatch",
            ArtifactTestKit.ProblemType(clash));
        Assert.Contains(await _fixture.Db.AuditRows(decision: "deny"),
            a => a.Action == "artifact.reregistration-denied"
                 && a.Resource.Contains(artifactId.ToString(), StringComparison.Ordinal));
        Assert.Equal("verified", (await _fixture.Db.Artifact(artifactId))!.Verification);
    }

    [Fact]
    public async Task CloudEventPlaneRegistersAndConfirmsIdentically()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "ce plane artifacts");

        // External form over /api/ingress/events behaves like the link arm.
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, "https://example.invalid/z.md", workId),
            HttpStatusCode.Accepted);
        var row = Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("external", row.Origin);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.registered"));

        // Confirmation form: a kernel-stored Guid's row is untouched, noted once.
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));
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
        Assert.Equal("verified", (await _fixture.Db.Artifact(artifactId))!.Verification);
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "artifact.available"));
    }

    /// <summary>The D6 screen: syntax laws first, binding waives the address
    /// screen, unbound URLs must resolve fully public. A miss never refuses —
    /// it downgrades to <c>unverified</c> with the reason audited.</summary>
    [Theory]
    [InlineData("http://user@host.example/x", "userinfo")]
    [InlineData("ftp://host.example/x", "not-absolute-http")]
    [InlineData("http://127.0.0.1/x", "private-address")]
    [InlineData("http://localhost/realm/realms/facilitator", "private-address")]
    public async Task UnboundOrUnsafeUrlDowngradesWithReason(string url, string reason)
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            factory.CreateAuthedClient(), WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, url),
            HttpStatusCode.Accepted);

        var row = Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("unverified", row.Verification);
        Assert.Contains(await _fixture.Db.AuditRows(), a =>
            a.Action == "artifact.registered"
            && a.Resource == $"artifact/{row.ArtifactId}"
            && ScreenOf(a) == reason);
    }

    [Fact]
    public async Task BoundPrefixUpgradesToVerifiedWithoutAddressScreen()
    {
        // Loopback would fail the address screen; the operator binding waives it.
        var settings = ProviderSettings.Concat(new Dictionary<string, string?>
        {
            ["Storage:ExternalBindings:0:Id"] = "keycloak",
            ["Storage:ExternalBindings:0:UrlPrefix"] = KeycloakFixture.Issuer,
        }).ToDictionary(kv => kv.Key, kv => kv.Value);
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: settings);
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            factory.CreateAuthedClient(), WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));
        var wireId = "artifact-" + ArtifactTestKit.B64(Guid.NewGuid());

        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.AvailableEnvelope(wireId, ForeignDigest, $"{KeycloakFixture.Issuer}/w/x.md"),
            HttpStatusCode.Accepted);

        var row = Assert.Single(await _fixture.Db.ArtifactByWireId(wireId));
        Assert.Equal("verified", row.Verification);
        Assert.Contains(await _fixture.Db.AuditRows(), a =>
            a.Action == "artifact.registered"
            && a.Resource == $"artifact/{row.ArtifactId}"
            && ScreenOf(a) == "bound:keycloak");
    }
}

/// <summary>Filesystem leg of the external-registration matrix.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactExternalRegistrationTests(PostgresFixture fixture) : ArtifactExternalRegistrationTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "kernel-fs:";
}

/// <summary>s3 leg of the external-registration matrix.</summary>
[Collection("postgres:work:s3")]
public sealed class S3ArtifactExternalRegistrationTests(PostgresFixture fixture) : ArtifactExternalRegistrationTestsBase(fixture)
{
    /// <inheritdoc />
    protected override string ExpectedScheme => "s3:";

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;
}
