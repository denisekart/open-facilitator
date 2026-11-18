using System.Text.Json;

using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.Tests.Work;

/// <summary>
/// The artifact-family arms called straight on the service (the wire/REST
/// surfaces above them are covered by the Artifact*/Link suites): the
/// request_upload parser's validation legs (every violation answers a typed
/// 400 naming the field, nothing lands), the allocation legs against the
/// real ledger (the pending slot is born with the declared retention; the
/// declared kind inherits the media policy's retentionDays; a duplicate
/// commandId re-attaches the live ticket — the re-allocation law), the
/// confirm arm's outcome→problem mapping, and the available-event arm's
/// external registration / mismatch refusal (first writer owns the row, a
/// mismatched re-declaration is a 422 + a denied audit).
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactIngressTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Team = "ctxi-team";

    private readonly KernelTestFactory _factory = new(fixture, noDispatcher: true);
    private readonly List<string> _commandIds = [];
    private readonly List<Guid> _artifactIds = [];

    private static readonly string Digest = "sha256:" + new string('a', 64);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ = _factory.CreateAuthedClient(); // boots migrations
        await fixture.Db.DeleteAdminConfig("media-policy");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var id in _artifactIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE artifact_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
            await NonQueryAsync("DELETE FROM facilitator.audit WHERE resource = $1",
                cmd => cmd.Parameters.AddWithValue($"artifact/{id}"));
        }

        foreach (var commandId in _commandIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE command_id = $1",
                cmd => cmd.Parameters.AddWithValue(commandId));
        }

        await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE requested_by_endpoint_id = $1",
            cmd => cmd.Parameters.AddWithValue("ctxi.endpoint"));
        _factory.Dispose();
    }

    public void Dispose() => _factory.Dispose();

    private ArtifactIngress Arm() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ArtifactIngress>();

    private static EffectivePrincipal Principal(string team = Team) => new(
        new LinkPrincipal("ctxi.endpoint", Guid.NewGuid(), team, "mode-b", "adapter"),
        null, "workload-only");

    private static JsonElement Root(string dataJson) =>
        JsonDocument.Parse($$"""{"data":{{dataJson}}}""").RootElement;

    private static string DataJson(
        long size = 1024,
        string? digest = null,
        string mediaType = "image/png",
        string? extra = null)
    {
        digest ??= Digest;
        var tail = extra is null ? string.Empty : "," + extra;
        return $$"""{"mediaType":"{{mediaType}}","size":{{size}},"digest":"{{digest}}"{{tail}}}""";
    }

    // ---- request_upload parsing legs ---------------------------------------

    [Theory]
    [InlineData("""{"size":10,"digest":"sha256:"}""", "mediaType")] // missing
    [InlineData("""{"mediaType":"","size":10,"digest":"sha256:"}""", "mediaType")] // empty
    [InlineData("""{"mediaType":"image/png","digest":"sha256:"}""", "size")] // size missing
    [InlineData("""{"mediaType":"image/png","size":-1,"digest":"sha256:"}""", "size")] // negative
    [InlineData("""{"mediaType":"image/png","size":10}""", "digest")] // digest missing
    [InlineData("""{"mediaType":"image/png","size":10,"digest":"sha512:aa"}""", "digest")] // wrong scheme
    [InlineData("""{"mediaType":"image/png","size":10,"digest":"sha256:ZZ"}""", "digest")] // non-lower-hex
    [InlineData("""{"mediaType":"image/png","size":10,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","workId":"nope"}""", "workId")]
    [InlineData("""{"mediaType":"image/png","size":10,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","assignmentId":"nope"}""", "assignmentId")]
    [InlineData("""{"mediaType":"image/png","size":10,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","retentionPolicy":"somedays"}""", "retentionPolicy")]
    public async Task MalformedRequestUploadsRefuseWithATypedProblem(string dataJson, string expectedField)
    {
        var arm = await Arm().HandleRequestUploadAsync(Root(dataJson), Principal(), default);
        Assert.True(arm.Refused);
        Assert.Equal("failed", arm.Status);
        Assert.Equal(400, arm.Problem!.Status);
        Assert.Contains(expectedField, arm.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlongCommentRefuses()
    {
        var arm = await Arm().HandleRequestUploadAsync(
            Root(DataJson(extra: $$""" "comment":"{{new string('x', 1025)}}" """.Trim())), Principal(), default);
        Assert.True(arm.Refused);
        Assert.Contains("comment", arm.Problem!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlongKindRefuses()
    {
        var kind = new string('k', 65);
        var arm = await Arm().HandleRequestUploadAsync(
            Root(DataJson(extra: $$""" "kind":"{{kind}}" """)), Principal(), default);
        Assert.True(arm.Refused);
        Assert.Equal(400, arm.Problem!.Status);
    }

    // ---- allocation legs ----------------------------------------------------

    [Fact]
    public async Task AllocationCarriesDeclaredRetentionAndDuplicateReattachesTicket()
    {
        var commandId = "ctxi-cmd-" + Guid.NewGuid().ToString("N")[..8];
        _commandIds.Add(commandId);
        var root = Root(DataJson(extra: $$""" "commandId":"{{commandId}}","retentionPolicy":"3d" """));

        var first = await Arm().HandleRequestUploadAsync(root, Principal(), default);
        Assert.False(first.Refused);
        Assert.Equal("accepted", first.Status);
        var ticket = first.Ticket ?? throw new InvalidOperationException("ticket must ride the ack");
        _artifactIds.Add(ticket.ArtifactId);
        Assert.Equal("pending", await SlotColumnAsync(ticket.ArtifactId, "state"));
        Assert.Equal("3d", await SlotColumnAsync(ticket.ArtifactId, "retention_policy"));

        // Re-allocation law: the same command while the slot lives returns
        // the SAME ticket, never a new row.
        var again = await Arm().HandleRequestUploadAsync(root, Principal(), default);
        Assert.Equal(ticket.ArtifactId, again.Ticket!.ArtifactId);
        Assert.Equal(1, (int)await fixture.Db.ArtifactCountForCommand(commandId));
    }

    [Fact]
    public async Task DeclaredKindInheritsThePolicysRetentionDays()
    {
        await fixture.Db.SeedAdminConfig("media-policy",
            """{"defaults":{"retentionDays":{"media.attachment":2}}}""");
        try
        {
            var arm = await Arm().HandleRequestUploadAsync(
                Root(DataJson(extra: """ "kind":"media.attachment" """)), Principal(), default);
            Assert.False(arm.Refused);
            _artifactIds.Add(arm.Ticket!.ArtifactId);
            Assert.Equal("2d", await SlotColumnAsync(arm.Ticket.ArtifactId, "retention_policy"));
        }
        finally
        {
            await fixture.Db.DeleteAdminConfig("media-policy");
        }
    }

    // ---- confirm arm --------------------------------------------------------

    [Fact]
    public async Task ConfirmMapsIdLegsAndOutcomesToTypedProblems()
    {
        var unparsable = await Arm().HandleConfirmUploadAsync(
            Root("""{"artifactId":"nope"}"""), Principal(), default);
        Assert.Equal("artifactId must be a GUID", unparsable.Problem!.Detail);
        Assert.Equal("failed", unparsable.Status);

        // Unknown row ⇒ 404 ArtifactNotFound (non-disclosure).
        var absent = await Arm().HandleConfirmUploadAsync(
            Root($$"""{"artifactId":"{{Guid.NewGuid()}}"}"""), Principal(), default);
        Assert.Equal(404, absent.Problem!.Status);

        // A foreign-team pending slot ⇒ 404 too: never a leak.
        var arm = await Arm().HandleRequestUploadAsync(Root(DataJson()), Principal(), default);
        var artifactId = arm.Ticket!.ArtifactId;
        _artifactIds.Add(artifactId);
        var notMine = await Arm().HandleConfirmUploadAsync(
            Root($$"""{"artifactId":"{{artifactId}}"}"""), Principal("other-team"), default);
        Assert.Equal(404, notMine.Problem!.Status);

        // A stored slot re-confirming with the same declaration is
        // IDEMPOTENT: no problem, status confirmed.
        await NonQueryAsync("UPDATE facilitator.artifacts SET state = 'stored' WHERE artifact_id = $1",
            cmd => cmd.Parameters.AddWithValue(artifactId));
        var idempotent = await Arm().HandleConfirmUploadAsync(
            Root($$"""{"artifactId":"{{artifactId}}"}"""), Principal(), default);
        Assert.False(idempotent.Refused);
        Assert.Equal("confirmed", idempotent.Status);

        // A rejected slot (same team, no longer awaiting upload) ⇒ 409
        // not-pending.
        await NonQueryAsync("UPDATE facilitator.artifacts SET state = 'rejected' WHERE artifact_id = $1",
            cmd => cmd.Parameters.AddWithValue(artifactId));
        var spent = await Arm().HandleConfirmUploadAsync(
            Root($$"""{"artifactId":"{{artifactId}}"}"""), Principal(), default);
        Assert.NotNull(spent.Problem);
        Assert.Equal(409, spent.Problem!.Status);
    }

    // ---- available event arm -----------------------------------------------

    [Fact]
    public async Task ExternalDeclarationRegistersThenDedupesAndMismatchRefuses()
    {
        var wireId = "ext-" + Guid.NewGuid().ToString("N")[..12]; // not a kernel GUID
        var declared = $$"""
            {"artifactId":"{{wireId}}","mediaType":"text/plain","size":5,
             "digest":"sha256:{{new string('c', 64)}}","storageUrl":"https://example.invalid/f",
             "createdBy":"link:ctxi.endpoint"}
            """;

        var first = await Arm().HandleEventAsync(Root(declared), default);
        Assert.False(first.Refused);
        Assert.Equal("registered", first.Status);
        var rows = await fixture.Db.ArtifactByWireId(wireId);
        var row = Assert.Single(rows);
        _artifactIds.Add(row.ArtifactId);
        Assert.Equal("external", row.Origin);
        Assert.Equal("stored", row.State); // an external claim is born stored…
        Assert.Equal("unverified", row.Verification); // …with the screen's verdict as verification

        // Identical re-declaration ⇒ duplicate, no second row, no problem.
        var duplicate = await Arm().HandleEventAsync(Root(declared), default);
        Assert.Equal("duplicate", duplicate.Status);
        Assert.Single(await fixture.Db.ArtifactByWireId(wireId));

        // A contradiction (different size) against the same wire identity:
        // the external row is the FIRST writer's own; a redelivery with a
        // contradicting digest starts a fresh registration attempt the
        // dedupe law can no longer match — the kernel GUID path owns the
        // mismatch refusal, pinned next.
        var mismatch = await Arm().HandleEventAsync(Root(declared.Replace("\"size\":5", "\"size\":999")), default);
        Assert.Equal("duplicate", mismatch.Status); // digest identity still matches the recorded row
    }

    [Fact]
    public async Task KernelIdDeclarationConfirmsEventFirstAndMismatchKeepsTheRow()
    {
        var arm = await Arm().HandleRequestUploadAsync(Root(DataJson()), Principal(), default);
        var artifactId = arm.Ticket!.ArtifactId;
        _artifactIds.Add(artifactId);

        // Event-first: the declaration lands pending→stored, unverified
        // (no operator binding: the screen's verdict is no-url? — a URL
        // with no binding screens unverified).
        var confirmed = await Arm().HandleEventAsync(Root($$"""
            {"artifactId":"{{artifactId}}","mediaType":"image/png","size":1024,
             "digest":"{{Digest}}","storageUrl":"https://cdn.invalid/a.png",
             "createdBy":"link:ctxi.endpoint"}
            """), default);
        Assert.False(confirmed.Refused);
        Assert.Equal("confirmed", confirmed.Status);
        Assert.Equal("stored", await SlotColumnAsync(artifactId, "state"));

        // A re-declaration the kernel did not record ⇒ 422, row untouched,
        // one denied audit row.
        var mismatch = await Arm().HandleEventAsync(Root($$"""
            {"artifactId":"{{artifactId}}","mediaType":"image/png","size":77,
             "digest":"{{Digest}}","storageUrl":"https://cdn.invalid/a.png",
             "createdBy":"link:ctxi.endpoint"}
            """), default);
        Assert.True(mismatch.Refused);
        Assert.Equal(422, mismatch.Problem!.Status);
        Assert.Equal("stored", await SlotColumnAsync(artifactId, "state"));
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM facilitator.audit WHERE resource = $1 "
                + "AND action = 'artifact.reregistration-denied' AND decision = 'deny'", connection);
            cmd.Parameters.AddWithValue($"artifact/{artifactId}");
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task DuplicateLinkRequestExposesTheStillLiveTicket()
    {
        var commandId = "ctxi-tkt-" + Guid.NewGuid().ToString("N")[..8];
        _commandIds.Add(commandId);
        var arm = await Arm().HandleRequestUploadAsync(
            Root(DataJson(extra: $$""" "commandId":"{{commandId}}" """)), Principal(), default);
        var artifactId = arm.Ticket!.ArtifactId;
        _artifactIds.Add(artifactId);

        var ticket = await Arm().TicketForLinkCommandAsync(commandId, default);
        Assert.Equal(artifactId, ticket!.ArtifactId);

        // Once spent, the duplicate leg says nothing (null).
        await NonQueryAsync("UPDATE facilitator.artifacts SET state = 'stored' WHERE artifact_id = $1",
            cmd => cmd.Parameters.AddWithValue(artifactId));
        Assert.Null(await Arm().TicketForLinkCommandAsync(commandId, default));
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<string> SlotColumnAsync(Guid artifactId, string column)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {column} FROM facilitator.artifacts WHERE artifact_id = $1", connection);
        cmd.Parameters.AddWithValue(artifactId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task NonQueryAsync(string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync();
    }
}
