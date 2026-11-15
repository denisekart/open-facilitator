using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Shared.Storage;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// The dispatch-time context fold (plan wave 4) and the retention leg of
/// wave 5, end to end through the routing funnel:
/// <list type="bullet">
///   <item>a route declaring <c>[envelope, work]</c> ⇒ the
///   <c>work.assign</c> row's <c>data.inputs.context.document</c> carries
///   the sources in block order (<c>sources[1].fields.objective</c> = the
///   work objective, the document digest-stamped) and a
///   <c>context.dispatch</c> artifact row exists, born stored+verified with
///   <c>RetentionPolicy == "7d"</c>;</item>
///   <item>a <c>thread://</c> block ⇒ the intent parks: a
///   <c>context-pending</c> routing decision, a <c>pendingFetches</c> audit
///   record with the deterministic fetchId, a
///   <c>context-fetch:&lt;fetchId&gt;</c> command on the source adapter's
///   outbox, and NO assign yet; the fabricated <c>event.context.fetched.v1</c>
///   ingress wakes the intent and the second pass emits the assign carrying
///   the history source;</item>
///   <item>a <c>context.history</c> artifact aged past <c>1d</c> is collected
///   by a forced <c>RunOnceAsync</c> sweep while a still-pinned
///   recorded-manifest output is not.</item>
/// </list>
/// Rides the dispatch-link container: the fold is only observable with a
/// dispatcher this test drives, and the wake law requeues the parked intent.
/// Every test works on its OWN hermetic team, so a foreign enrollment can
/// never share a candidate slot.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ContextBuildTests(DispatchDbFixture fixture) : IAsyncLifetime
{
    private const string Objective = "annotate the crack in the hull plate";
    private const string Capability = "research.analyze";

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() =>
        // A leaked live fleet could join a later run's candidate set. The
        // media-policy row is the Work collection's to own (the media policy
        // scenario writes it); a cross-collection purge could yank it from
        // under that test mid-run.
        fixture.Db.PurgeFleets("ctxb.");

    private static string Leaf() => Guid.NewGuid().ToString("N")[..10];

    private static string ManifestFor(string team) =>
        $$"""
        {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"{{team}}",
         "capabilities":[{"id":"{{Capability}}","version":1,"execution":"asynchronous","acceptedContexts":["project"]}]}
        """;

    /// <summary>A per-test hermetic team + live fleet advertising the
    /// capability, on a factory configured by <paramref name="settings"/>
    /// (which receives the fleet endpoint id).</summary>
    private async Task<(string Team, string Endpoint, KernelTestFactory Factory, HttpClient Kernel, HttpClient Link)>
        FleetAsync(Func<string, Dictionary<string, string?>> settings)
    {
        var team = $"ctxb-w-{Leaf()}";
        var endpoint = $"ctxb.{Leaf()}";
        await fixture.Db.SeedTeam(team);
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings(endpoint));
        var kernel = factory.CreateAuthedClient();
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint, team: team);
        await fixture.Db.SeedCapabilityManifest(endpoint, ManifestFor(team));
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow - TimeSpan.FromHours(2));
        var link = factory.CreateLinkClient(token);
        return (team, endpoint, factory, kernel, link);
    }

    /// <summary>Mattermost-provider settings pointing the assign pair at the
    /// fleet endpoint, carrying the block rows under test.</summary>
    private static Dictionary<string, string?> ProviderSettings(
        string endpoint, Dictionary<string, string?> block) => new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:AssignEndpointId"] = endpoint,
            ["Providers:Mattermost:AssignCapabilityId"] = Capability,
        }.Concat(block).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Create a work carrying one routing assignment (the funnel
    /// claim) under the fleet team.</summary>
    private async Task<Guid> CreateRoutableWorkAsync(HttpClient kernel, string team)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["objective"] = Objective,
            ["projectId"] = await fixture.Db.SeedProject(team),
            ["commandId"] = Guid.NewGuid().ToString("N"),
            ["assignments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["capabilityId"] = Capability,
                    ["objective"] = Objective,
                    ["targetId"] = "kernel",
                    ["actorEvidenceRef"] = "identity-evidence:ctx-build",
                },
            },
        });
        var created = await KernelOps.PostJsonAsync(kernel, "/api/works", body, HttpStatusCode.Created);
        return created.GetProperty("workId").GetGuid();
    }

    [Fact]
    public async Task EnvelopeWorkBlockFoldsTheDocumentIntoTheAssignAndStoresTheDispatchArtifact()
    {
        var (team, _, factory, kernel, link) = await FleetAsync(endpoint => ProviderSettings(endpoint, new Dictionary<string, string?>
        {
            ["Providers:Mattermost:AssignContext:0:Envelope"] = "required",
            ["Providers:Mattermost:AssignContext:1:Work:Fields:0"] = "objective",
        }));
        await using (factory)
        using (kernel)
        using (link)
        {
            var workId = await CreateRoutableWorkAsync(kernel, team);

            var effect = await RoutingHarness.DrainUntilAsync(
                factory,
                () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"),
                "assign effect carrying the folded context");
            using var doc = JsonDocument.Parse(effect);
            var context = doc.RootElement.GetProperty("data").GetProperty("inputs").GetProperty("context");
            var document = context.GetProperty("document");
            Assert.Equal("context.dispatch.v1", document.GetProperty("schema").GetString());
            Assert.StartsWith("sha256:", document.GetProperty("digest").GetString());
            var sources = document.GetProperty("sources").EnumerateArray().ToList();
            Assert.Equal(2, sources.Count);
            Assert.Equal("context[0]", sources[0].GetProperty("path").GetString());
            Assert.Equal("envelope", sources[0].GetProperty("key").GetString());
            Assert.Equal("work", sources[1].GetProperty("key").GetString());
            // The plan's exact probe: sources[1].fields.objective equals the
            // work objective.
            Assert.Equal(
                Objective,
                sources[1].GetProperty("fields").GetProperty("objective").GetString());
            // The assign's own context reference names the stored doc.
            Assert.Equal(document.GetProperty("digest").GetString(), context.GetProperty("digest").GetString());

            // The document is evidence: a context.dispatch artifact row,
            // stored, digest-stamped, 7d retention, ledgered under the
            // effect command.
            var artifact = Assert.Single(await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch"));
            Assert.Equal("7d", artifact.RetentionPolicy);
            Assert.Equal("stored", artifact.State);
            Assert.Equal(document.GetProperty("digest").GetString(), artifact.Digest);
            Assert.Equal($"context-dispatch:work-assign:{workId:N}", artifact.CommandId);

            // One audit row names the build.
            Assert.Contains(await fixture.Db.AuditRowsFor("context.dispatch"),
                a => a.Resource == $"work/{workId}" && a.Reason == "built");
        }
    }

    [Fact]
    public async Task ThreadBlockParksTheIntentAndTheFetchedAnswerEmitsTheAssignOnTheSecondPass()
    {
        var (team, endpoint, factory, kernel, link) = await FleetAsync(endpoint => ProviderSettings(endpoint, new Dictionary<string, string?>
        {
            ["Providers:Mattermost:AssignContext:0:Envelope"] = "required",
            ["Providers:Mattermost:AssignContext:1:History:Last"] = "5",
        }));
        await using (factory)
        using (kernel)
        using (link)
        {
            var workId = await CreateRoutableWorkAsync(kernel, team);
            var threadUri = $"thread://mattermost/chan-{workId:N}/root-{workId:N}";
            await fixture.Db.SeedBinding("mattermost", $"chan-{workId:N}", $"root-{workId:N}", workId);

            // Pass 1: no answer yet ⇒ the intent parks — audit first, no
            // assign effect.
            var parked = await RoutingHarness.DrainUntilAsync(
                factory,
                async () =>
                {
                    var row = await fixture.Db.RoutingDecision($"routing:work-assign:{workId:N}");
                    return row is { Status: "pending", Basis: "context-pending" } ? row : null;
                },
                "context-pending routing decision");
            Assert.Equal("context-pending", parked.Basis);
            Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"));

            // The 3.3 record: pendingFetches with the deterministic fetchId …
            var fetchAudit = (await fixture.Db.AuditRowsFor("context.fetch"))
                .Single(a => a.Resource == $"work/{workId}" && a.Detail.Contains("pendingFetches", StringComparison.Ordinal));
            using (var fetchDoc = JsonDocument.Parse(fetchAudit.Detail))
            {
                var entry = Assert.Single(fetchDoc.RootElement.GetProperty("pendingFetches").EnumerateArray());
                Assert.Equal(threadUri, entry.GetProperty("uri").GetString());
                var fetchId = entry.GetProperty("fetchId").GetString();
                Assert.Equal(ContextBuilder.FetchIdFor($"work-assign:{workId:N}", threadUri), fetchId);

                // … and the 3.2 leg: the SAME fetchId names the routed
                // command on the source adapter's endpoint outbox.
                var routed = await fixture.Db.OutboxEnvelopeByCommand($"context-fetch:{fetchId}");
                Assert.NotNull(routed);
                Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));
            }

            // The adapter's asynchronous answer, fabricated over the link
            // plane (the same ingress a real adapter drives).
            var answer = $$"""
                {
                  "specversion": "1.0",
                  "id": "ctx-ans-{{Leaf()}}",
                  "source": "urn:facilitator:instance:main",
                  "type": "org.facilitator.event.context.fetched.v1",
                  "subject": "work/{{workId}}",
                  "time": "2026-11-15T18:05:00Z",
                  "datacontenttype": "application/json",
                  "dataschema": "https://schemas.openfacilitator.org/v1/event.context.fetched.v1.json",
                  "data": {
                    "fetchId": "{{ContextBuilder.FetchIdFor($"work-assign:{workId:N}", threadUri)}}",
                    "workId": "{{workId}}",
                    "responder": "mattermost",
                    "items": [
                      {
                        "uri": "{{threadUri}}",
                        "content": {"messages": [
                          {"id": "455", "text": "can you annotate this?"},
                          {"id": "456", "text": "please mark the crack in the hull plate"}
                        ]},
                        "truncated": false
                      }
                    ]
                  }
                }
                """;
            await KernelOps.PostJsonAsync(link, "/api/link/v1/events", answer, HttpStatusCode.Accepted);

            // Pass 2: the wake law requeues the parked intent; the answer
            // now folds in as a history source and the assign lands.
            var effect = await RoutingHarness.DrainUntilAsync(
                factory,
                () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"),
                "assign effect with the history source");
            using var doc = JsonDocument.Parse(effect);
            var sources = doc.RootElement.GetProperty("data").GetProperty("inputs")
                .GetProperty("context").GetProperty("document").GetProperty("sources")
                .EnumerateArray().ToList();
            var history = Assert.Single(sources, s => s.GetProperty("key").GetString() == "history");
            Assert.Equal(threadUri, history.GetProperty("uri").GetString());
            Assert.Equal(
                "please mark the crack in the hull plate",
                history.GetProperty("answer").GetProperty("content").GetProperty("messages")[1]
                    .GetProperty("text").GetString());

            // The answered thread is durable evidence too: a context.history
            // artifact row from the ingress leg.
            Assert.Single(await fixture.Db.ArtifactRowsForKind(workId, "context.history"));
        }
    }

    [Fact]
    public async Task AgedContextHistoryIsCollectedWhileAPinnedManifestOutputSurvives()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ofac-ctxgc-{Guid.NewGuid():N}");
        using var factory = new KernelTestFactory(fixture, noDispatcher: true,
            settings: new Dictionary<string, string?> { ["Storage:FileSystem:RootPath"] = root });
        var gc = factory.Services.GetRequiredService<RetentionGcService>();
        var aged = Guid.NewGuid();
        var pinned = Guid.NewGuid();
        var agedKey = $"aa/{aged:N}";
        var agedBlob = Path.Combine(root, agedKey);
        Directory.CreateDirectory(Path.GetDirectoryName(agedBlob)!);
        await File.WriteAllTextAsync(agedBlob, "answered thread history");
        var old = DateTime.UtcNow.AddDays(-2);
        await fixture.Db.SeedArtifact(aged, "stored", "1d", old, workId: null, storageUri: $"kernel-fs:{agedKey}");
        await fixture.Db.SeedArtifactKind(aged, "context.history");
        await fixture.Db.SeedArtifact(pinned, "stored", "1d", old, workId: null);
        await fixture.Db.SeedArtifactKind(pinned, "result.image");
        // The manifest is the durable claim: its recorded outputs are pinned
        // regardless of age.
        var pinWork = await fixture.Db.SeedWorkOwned(
            await fixture.Db.SeedProject("team-alpha"), null, "review", "team-alpha");
        await fixture.Db.SeedResultManifest(
            $"manifest-{pinned:N}", pinWork, pinned, [pinned.ToString("N")], "recorded", "validated");

        await gc.RunOnceAsync(default);

        Assert.Equal("deleted", (await fixture.Db.Artifact(aged))!.State);
        Assert.False(File.Exists(agedBlob));
        Assert.Equal("stored", (await fixture.Db.Artifact(pinned))!.State);
        Assert.Contains(await fixture.Db.AuditRowsFor("artifact.gc"),
            a => a.Resource == $"artifact/{aged}" && a.Reason == "retention");
    }
}
