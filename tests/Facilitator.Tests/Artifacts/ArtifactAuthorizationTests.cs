using System.Net;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Non-disclosure law: every foreign-team access — human read, human PUT,
/// workload PUT — answers the SAME 404 as a missing row (the approvals
/// work-scope precedent). A principal without any team group is refused at
/// the role gate before the ledger is touched. Provider-matrixed: the law
/// holds identically on the filesystem and s3 legs.
/// </summary>
public abstract class ArtifactAuthorizationTestsBase
{
    private readonly PostgresFixture _fixture;

    protected ArtifactAuthorizationTestsBase(PostgresFixture fixture) => _fixture = fixture;

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


    [Fact]
    public async Task ForeignTeamReadsAndWritesAnswerAbsent()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var token = await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret");
        var link = factory.CreateLinkClient(token);

        var commandId = "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid());
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(commandId, ArtifactTestKit.ReportBytes, "identity-evidence:opaque"),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);

        // bob (team-beta, groups but no operator role): passes the gate on the
        // team-member role, the ledger answers the row absent.
        var bob = await ArtifactTestKit.UserClientAsync(factory, "bob");
        var meta = await bob.GetAsync($"/api/artifacts/{artifactId}");
        Assert.Equal(HttpStatusCode.NotFound, meta.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/artifact-not-found", ArtifactTestKit.ProblemType(meta));

        // bob's REST PUT: the write binding is operator-only, so the gate
        // refuses before the ledger (403 is as non-disclosing as 404).
        var bobPut = await ArtifactTestKit.PutBytesAsync(bob, $"/api/artifacts/{artifactId}/content", ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.Forbidden, bobPut.StatusCode);

        // A team-beta WORKLOAD (mode-a secret enrollment, role child so no gw. prefix law):
        // the link-plane PUT route guards on row team ownership -> 404.
        var betaToken = await LinkTestHarness.EnrollAsync(kernel, $"art.beta{LegTag}", team: "team-beta");
        var beta = factory.CreateLinkClient(betaToken);
        var betaPut = await ArtifactTestKit.PutBytesAsync(beta, $"/api/link/v1/artifacts/{artifactId}/content", ArtifactTestKit.ReportBytes);
        Assert.Equal(HttpStatusCode.NotFound, betaPut.StatusCode);

        // alice (framework-admin) sees it.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/artifacts/{artifactId}")).StatusCode);

        // carol has no group claim: the team-member rule denies, the gate 403s.
        var carol = await ArtifactTestKit.UserClientAsync(factory, "carol");
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.GetAsync($"/api/artifacts/{artifactId}")).StatusCode);

        // Anonymous has nothing.
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/artifacts/{artifactId}")).StatusCode);
    }

    [Fact]
    public async Task PendingSlotIsNotReadableContent()
    {
        using var factory = new KernelTestFactory(_fixture, noDispatcher: true, settings: ProviderSettings);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await ArtifactTestKit.EnrollModeBAsync(
            kernel, WorkloadEndpoint, "ofac-gateway-alpha", "gateway-alpha-secret"));

        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            ArtifactTestKit.RequestUploadEnvelope(
                "cmd-art-" + ArtifactTestKit.B64(Guid.NewGuid()), ArtifactTestKit.ReportBytes, "identity-evidence:opaque"),
            HttpStatusCode.Accepted);
        var artifactId = Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);

        // Metadata visible to the owning team while pending, content absent until stored.
        var alice = await ArtifactTestKit.UserClientAsync(factory, "alice");
        var meta = await KernelOps.GetJsonAsync(alice, $"/api/artifacts/{artifactId}", HttpStatusCode.OK);
        Assert.Equal("pending", meta.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/artifacts/{artifactId}/content")).StatusCode);
    }
}

/// <summary>Filesystem leg of the authorization matrix.</summary>
[Collection("postgres:work")]
public sealed class FsArtifactAuthorizationTests(PostgresFixture fixture) : ArtifactAuthorizationTestsBase(fixture);

[Collection("postgres:work:s3")]
public sealed class S3ArtifactAuthorizationTests(PostgresFixture fixture) : ArtifactAuthorizationTestsBase(fixture)
{
    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => ArtifactTestKit.S3Settings;
}
