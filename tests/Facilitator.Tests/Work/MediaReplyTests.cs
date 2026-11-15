using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// The completion-reply media arm: a terminal-COMPLETED transition of a bound
/// work on a provider with <c>SendManifestArtifacts</c> reads the recorded
/// manifest's stored+verified outputs, filters them by the endpoint's declared
/// <c>media</c> facet, and either attaches them to the
/// <c>communication.reply.v1</c> row (<c>kind == "result.image"</c>) or
/// degrades honestly — the reply text names a brokered link (or the artifact
/// id when <c>OFAC:PublicBaseUrl</c> is unset) and one audit row per drop
/// names the machine reason. No facet ⇒ nothing attaches, nothing vanishes
/// silently. The team's <c>media-policy</c> admin resource narrows the
/// facet further: a <c>forbidKinds</c> entry for the work's team makes the
/// same image degrade even against an image-capable facet — the policy is
/// demonstrably consulted, not decorative (the policy scenario restores
/// global state by purging the row it wrote).
/// </summary>
[Collection("postgres:work")]
public sealed class MediaReplyTests(PostgresFixture fixture)
{
    private static string ManifestJson(string facetJson) =>
        "{\"apiVersion\":\"facilitator.framework/v1\",\"kind\":\"CapabilityManifest\",\"ownerTeam\":\"team-alpha\","
        + "\"capabilities\":[{\"id\":\"org.facilitator.command.communication.reply.v1\",\"version\":1,"
        + "\"execution\":\"synchronous\","
        + facetJson
        + "}]}";

    private const string ImageFacet = @"""media"":{""directions"":[""in"",""out""],""kinds"":[""image/*""],""maxCount"":8,""maxBytesPerItem"":8388608}";

    private const string TextOnlyFacet = @"""media"":{""directions"":[""in"",""out""],""kinds"":[]}";

    private async Task<(KernelTestFactory Factory, Guid WorkId, Guid ArtifactId, string Endpoint, string CommandId)>
        CompletedWorkWithImageAsync(bool facetAllowsImage, string? publicBaseUrl, long artifactSize = 40960)
    {
        var endpoint = "gw-media-" + Guid.NewGuid().ToString("N")[..8];
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:SendManifestArtifacts"] = "true",
        };
        if (publicBaseUrl is not null)
        {
            settings["OFAC:PublicBaseUrl"] = publicBaseUrl;
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint);
        await fixture.Db.SeedCapabilityManifest(
            endpoint,
            ManifestJson(facetAllowsImage ? ImageFacet : TextOnlyFacet));

        var projectId = await fixture.Db.SeedProject("team-alpha");
        var workId = await fixture.Db.SeedWorkOwned(projectId, null, "review", "team-alpha");
        var artifactId = Guid.NewGuid();
        await fixture.Db.SeedStoredArtifact(artifactId, "image/png", artifactSize, workId, "team-alpha", endpoint);
        await fixture.Db.SeedResultManifest(
            $"manifest-{workId:N}", workId, artifactId, [artifactId.ToString("N")], "recorded", "validated");
        await fixture.Db.SeedBinding("mattermost", $"chan-{workId:N}", $"root-{workId:N}", workId);

        // review → completed through the coordination seam: the completion
        // hook runs inline, exactly as the HTTP transition surface runs it.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICoordinationStore>();
        var transition = await store.ApplyTransitionAsync(
            workId, WorkState.Completed, 1, "oidc:https://issuer.test|media-tester", "done", CancellationToken.None);
        Assert.IsType<TransitionResult.Ok>(transition);
        var reply = (await fixture.Db.OutboxEnvelopes(workId))
            .FirstOrDefault(e => e.Contains("command.communication.reply.v1", StringComparison.Ordinal));
        Assert.NotNull(reply);
        var commandId = JsonDocument.Parse(reply).RootElement
            .GetProperty("data").GetProperty("idempotencyKey").GetString();
        Assert.NotNull(commandId);
        return (factory, workId, artifactId, endpoint, commandId);
    }

    [Fact]
    public async Task OutImageFacetAttachesTheManifestArtifactToTheReply()
    {
        var (factory, _, artifactId, _, commandId) =
            await CompletedWorkWithImageAsync(facetAllowsImage: true, "https://kernel.example");
        using (factory)
        {
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("Work reached completed.", data.GetProperty("text").GetString());
            var artifact = Assert.Single(data.GetProperty("artifacts").EnumerateArray());
            Assert.Equal("result.image", artifact.GetProperty("kind").GetString());
            Assert.Equal(artifactId.ToString("N"), artifact.GetProperty("artifactId").GetString());
            Assert.Equal("image/png", artifact.GetProperty("mediaType").GetString());
            Assert.StartsWith("artifact://", artifact.GetProperty("storageUrl").GetString());

            // One audit row names the attach; no degrade row exists for this
            // reply (the shared DB holds other tests' rows, so probe by
            // correlation, not globally).
            var detail = await fixture.Db.AuditDetailByCorrelation("media.reply", commandId);
            Assert.NotNull(detail);
            Assert.Contains(artifactId.ToString("N"), detail);
            Assert.Null(await fixture.Db.AuditDetailByCorrelation("media.degrade", commandId));
        }
    }

    [Fact]
    public async Task TextOnlyFacetDegradesWithALinkLineAndAnAuditRow()
    {
        var (factory, _, artifactId, _, commandId) =
            await CompletedWorkWithImageAsync(facetAllowsImage: false, "https://kernel.example");
        using (factory)
        {
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.TryGetProperty("artifacts", out _));

            // The public link line stands in for the attachment; the audit
            // row names why ("kind-not-allowed") — a queryable answer.
            var text = data.GetProperty("text").GetString();
            Assert.StartsWith("Work reached completed.", text);
            Assert.Contains(
                $"attachment available: https://kernel.example/api/artifacts/{artifactId:N}/content",
                text,
                StringComparison.Ordinal);
            var degrade = await fixture.Db.AuditDetailByCorrelation("media.degrade", commandId);
            Assert.NotNull(degrade);
            Assert.Contains("kind-not-allowed", degrade, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoPublicBaseUrlDegradesToTheArtifactIdNote()
    {
        var (factory, _, artifactId, _, commandId) =
            await CompletedWorkWithImageAsync(facetAllowsImage: false, publicBaseUrl: null);
        using (factory)
        {
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope);
            var text = doc.RootElement.GetProperty("data").GetProperty("text").GetString();
            Assert.Contains($"attachment available: artifact {artifactId:N}", text, StringComparison.Ordinal);
            var degrade = await fixture.Db.AuditDetailByCorrelation("media.degrade", commandId);
            Assert.NotNull(degrade);
            Assert.Contains("no-public-base-url", degrade, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OversizedArtifactDegradesWithTheSizeCapReason()
    {
        // The facet caps at 8 MB; a 9 MB stored image must not attach.
        var (factory, _, artifactId, _, commandId) =
            await CompletedWorkWithImageAsync(facetAllowsImage: true, "https://kernel.example", 9 * 1024 * 1024);
        using (factory)
        {
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            using var doc = JsonDocument.Parse(envelope);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.TryGetProperty("artifacts", out _));
            Assert.Contains(
                $"attachment available: https://kernel.example/api/artifacts/{artifactId:N}/content",
                data.GetProperty("text").GetString(),
                StringComparison.Ordinal);
            var degrade = await fixture.Db.AuditDetailByCorrelation("media.degrade", commandId);
            Assert.NotNull(degrade);
            Assert.Contains("size-cap", degrade, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TeamMediaPolicyForbiddingImageDegradesDespiteAnImageFacet()
    {
        // The wave-1 success scenario (image facet + public base url) against
        // a media-policy row forbidding image/* for the work team: the facet
        // alone would attach, so a degrade here can only come from the
        // policy — the policy is consulted, not decorative. The reply
        // carries the brokered link line; the audit row names the machine
        // reason (kind-forbidden, the forbid-list veto).
        var (factory, _, _, endpoint, _) =
            await CompletedWorkWithImageAsync(facetAllowsImage: true, "https://kernel.example");
        try
        {
        using (factory)
        using (var kernel = factory.CreateAuthedClient())
        {
            using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/admin/config/media-policy")
            {
                Content = new StringContent(
                    """{"teams":[{"team":"team-alpha","forbidKinds":["image/*"]}]}""",
                    Encoding.UTF8, "application/json"),
            };
            patch.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
            patch.Headers.Add("X-Change-Reason", "policy consulted scenario");
            using (var written = await kernel.SendAsync(patch))
            {
                Assert.Equal(HttpStatusCode.OK, written.StatusCode);
            }

            // The arm consults the policy per completion reply: a second
            // bound work completed under the now-forbidding policy degrades.
            var workId = await fixture.Db.SeedWorkOwned(
                await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");
            var artifactId = Guid.NewGuid();
            await fixture.Db.SeedStoredArtifact(
                artifactId, "image/png", 40960, workId, "team-alpha", endpoint);
            await fixture.Db.SeedResultManifest(
                $"manifest-{workId:N}", workId, artifactId, [artifactId.ToString("N")], "recorded", "validated");
            await fixture.Db.SeedBinding("mattermost", $"chan-{workId:N}", $"root-{workId:N}", workId);
            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ICoordinationStore>();
            var transition = await store.ApplyTransitionAsync(
                workId, WorkState.Completed, 1, "oidc:https://issuer.test|media-tester", "done",
                CancellationToken.None);
            Assert.IsType<TransitionResult.Ok>(transition);
            var reply = (await fixture.Db.OutboxEnvelopes(workId))
                .FirstOrDefault(e => e.Contains("command.communication.reply.v1", StringComparison.Ordinal));
            Assert.NotNull(reply);
            var commandId = JsonDocument.Parse(reply!).RootElement
                .GetProperty("data").GetProperty("idempotencyKey").GetString();
            Assert.NotNull(commandId);

            using var doc = JsonDocument.Parse(
                await fixture.Db.OutboxEnvelopeByCommand(commandId!) ?? reply!);
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.TryGetProperty("artifacts", out _));
            Assert.Contains(
                $"attachment available: https://kernel.example/api/artifacts/{artifactId:N}/content",
                data.GetProperty("text").GetString(),
                StringComparison.Ordinal);
            var degrade = await fixture.Db.AuditDetailByCorrelation("media.degrade", commandId!);
            Assert.NotNull(degrade);
            Assert.Contains("kind-forbidden", degrade, StringComparison.Ordinal);
        }
        }
        finally
        {
            // Global config row: leave no trace for the parallel suites.
            await fixture.Db.AdminConfigPurge("media-policy");
        }
    }
}
