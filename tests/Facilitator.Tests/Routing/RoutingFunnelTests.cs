using System.Net;
using System.Text;
using System.Text.Json;



namespace Facilitator.Tests.Routing;

/// <summary>
/// The routing funnel: intents ride the dispatcher's routing channel and the
/// funnel is the ONLY producer of work.assign rows. Candidates = live
/// sessions on unrevoked enrollments advertising the capability, cross-team
/// excluded, deterministic order (specificity → oldest-online → endpointId);
/// preferences and the advisory provider only choose within the allowed set;
/// no route requeues forever with the reason on the audit row (no-candidate
/// vs no-capacity), and a crash-replay lands on the effect-command ledger.
/// Hermeticity: every test works on its OWN team, so a leaked enrollment
/// from any other suite can never share a candidate slot, and every routing
/// assertion polls (foreign background dispatchers over the shared outbox
/// may claim an intent outside this test's drains).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class RoutingFunnelTests(DispatchDbFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() =>
        // A leaked live fleet would join a later run's candidate set.
        fixture.Db.PurgeFleets("rt.");

    [Fact]
    public async Task SingleLiveCandidateRoutesOnceWithLedgerEffectAndAudit()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var endpoint = $"rt.solo{Leaf()}";
        await AdvertiseAsync(kernel, endpoint, team, Manifest(Analyse("research.analyze")));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        // The effect: one work.assign row addressed to the chosen endpoint.
        var effect = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"), "assign effect");
        var envelope = JsonDocument.Parse(effect);
        Assert.Equal("org.facilitator.command.work.assign.v1", envelope.RootElement.GetProperty("type").GetString());
        Assert.StartsWith($"work/{workId}/assignment/", envelope.RootElement.GetProperty("subject").GetString());
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));

        var audit = await fixture.Db.RoutingDecision($"routing:work-assign:{workId:N}");
        Assert.NotNull(audit);
        Assert.Equal("routed", audit!.Status);
        Assert.Equal(endpoint, audit.ChosenEndpointId);
        Assert.Equal("deterministic-order", audit.Basis);
        Assert.Equal(new[] { endpoint }, JsonSerializer.Deserialize<string[]>(audit.AllowedCandidates));
        Assert.Equal(1, audit.AttemptCount);

        // The assignment ledger row exists in its routed first generation.
        var assignments = await fixture.Db.AssignmentRowsForWork(workId);
        Assert.Equal(new[] { ("routed", "research.analyze", 1) }, assignments);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "assignment.transition"));
    }

    [Fact]
    public async Task MoreSpecificDescriptorOutranksOldestOnline()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var plain = $"rt.plain{Leaf()}";
        var rich = $"rt.rich{Leaf()}";
        // The plainer fleet opened FIRST: specificity must outrank it.
        await AdvertiseAsync(kernel, plain, team, Manifest(Analyse("research.analyze")), openedBefore: true);
        await AdvertiseAsync(kernel, rich, team,
            Manifest(Analyse("research.analyze", inputSchema: true, outputKinds: true, mediaTypes: true, contexts: false)));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal("routed", audit.Status);
        Assert.Equal(rich, audit.ChosenEndpointId);
        Assert.Equal(new[] { rich, plain }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            JsonSerializer.Deserialize<string[]>(audit.AllowedCandidates)!.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task EqualRankingFallsToOldestOnlineSession()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var old = $"rt.old{Leaf()}";
        var fresh = $"rt.fresh{Leaf()}";
        await AdvertiseAsync(kernel, fresh, team, Manifest(Analyse("research.analyze")));
        await AdvertiseAsync(kernel, old, team, Manifest(Analyse("research.analyze")), openedBefore: true);
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal(old, audit.ChosenEndpointId);
    }

    [Fact]
    public async Task NobodyAdvertisedPendsWithNoCandidateAndNeverDeadLetters()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.ghost");

        // Six passes minimum: the audit counts every claim, so poll until at
        // least six landed (a foreign dispatcher's pass counts too).
        var audit = await RoutingHarness.DrainUntilAsync(
            factory,
            async () =>
            {
                await fixture.Db.ExpireOutboxBackoff(workId);
                var row = await fixture.Db.RoutingDecision($"routing:work-assign:{workId:N}");
                return row is { AttemptCount: >= 6 } ? row : null;
            },
            "six pending routing passes");
        Assert.Equal("pending", audit!.Status);
        Assert.Equal("no-candidate", audit.Basis);

        // Never dead-lettered: the intent row is still a live requeue candidate.
        Assert.Equal(0, await fixture.Db.DeadLetterCount(workId, "no-candidate"));
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "pending"));
    }

    [Fact]
    public async Task AdvertisedButOfflinePendsWithNoCapacity()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var endpoint = $"rt.dark{Leaf()}";
        // Enrollment + manifest, but NO live session: the scale-up signal.
        await fixture.Db.SeedGatewayEnrollment(endpoint, team, "isolated-executor");
        await fixture.Db.SeedCapabilityManifest(endpoint, Manifest(Analyse("research.analyze")).Replace("{{TEAM}}", team));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal("pending", audit!.Status);
        Assert.Equal("no-capacity", audit.Basis);
        Assert.Equal("[]", audit.AllowedCandidates);
    }

    [Fact]
    public async Task CrossTeamCandidateIsExcludedEvenWhenLive()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var (otherTeam, _) = await FreshTeamAsync();
        var beta = $"rt.beta{Leaf()}";
        await AdvertiseAsync(kernel, beta, otherTeam, Manifest(Analyse("research.analyze")));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal("pending", audit!.Status);
        Assert.Equal("no-candidate", audit.Basis); // beta advertised, into beta's slot only
        Assert.DoesNotContain($"link.{beta}", await fixture.Db.OutboxRecipients(workId));
    }

    [Fact]
    public async Task PreferenceWithinAllowedSetIsSatisfiedAndAudited()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var plain = $"rt.p1{Leaf()}";
        var rich = $"rt.p2{Leaf()}";
        await AdvertiseAsync(kernel, plain, team, Manifest(Analyse("research.analyze")), openedBefore: true);
        await AdvertiseAsync(kernel, rich, team,
            Manifest(Analyse("research.analyze", inputSchema: true, outputKinds: true, mediaTypes: true, contexts: false)));

        // Prefer the deterministic loser: the funnel must honor the hint.
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze", preference: plain);

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal("routed", audit!.Status);
        Assert.Equal(plain, audit.ChosenEndpointId);
        Assert.Equal("preference-satisfied", audit.Basis);
    }

    [Fact]
    public async Task PreferenceOutsideAllowedSetFallsBackDeterministically()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var live = $"rt.p3{Leaf()}";
        var phantom = $"rt.p4-ghost{Leaf()}";
        await AdvertiseAsync(kernel, live, team, Manifest(Analyse("research.analyze")));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze", preference: phantom);

        var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
        Assert.Equal("routed", audit!.Status);
        Assert.Equal(live, audit.ChosenEndpointId);
        Assert.Equal("preference-ignored", audit.Basis);
    }

    [Fact]
    public async Task ReservedModelProviderFallsBackDeterministicAudited()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.AdminConfigPurge("routing-defaults");
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/admin/config/routing-defaults");
        request.Content = new StringContent("""{"provider": "model"}""", Encoding.UTF8, "application/json");
        request.Headers.IfMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Any);
        request.Headers.Add("X-Change-Reason", "routing suite");
        using var patchResponse = await kernel.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        try
        {
            var (team, _) = await FreshTeamAsync();
            var endpoint = $"rt.model{Leaf()}";
            await AdvertiseAsync(kernel, endpoint, team, Manifest(Analyse("research.analyze")));
            var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

            var audit = await DrainAuditAsync(factory, $"routing:work-assign:{workId:N}");
            Assert.Equal("routed", audit!.Status);
            Assert.Equal(endpoint, audit.ChosenEndpointId);
            Assert.Equal("model-fallback", audit.Basis);
            Assert.Equal("model", audit.ProviderRef);
        }
        finally
        {
            await fixture.Db.AdminConfigPurge("routing-defaults");
        }
    }

    [Fact]
    public async Task IntentReplayCollidesOnTheEffectLedgerAndAssignsOnce()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var (team, _) = await FreshTeamAsync();
        var endpoint = $"rt.replay{Leaf()}";
        await AdvertiseAsync(kernel, endpoint, team, Manifest(Analyse("research.analyze")));
        var (workId, _) = await CreateWorkAsync(kernel, team, "research.analyze");

        await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"), "assign effect");
        Assert.Single(await fixture.Db.AssignmentRowsForWork(workId));

        // Crash-replay: the effect committed but the intent's delivered-mark
        // did not — a second pass must NOT fork a second assignment or effect.
        await fixture.Db.RequeueOutboxByCommand($"routing:work-assign:{workId:N}");
        var audit = await DrainAuditAsync(
            factory, $"routing:work-assign:{workId:N}",
            row => row.AttemptCount >= 2 ? row : null);

        Assert.Single(await fixture.Db.AssignmentRowsForWork(workId));
        Assert.Equal(1, CountAssignRows(await fixture.Db.OutboxEnvelopes(workId)));
        Assert.Equal(2, audit!.AttemptCount); // the replay audited onto the same row
    }

    // --- plumbing -------------------------------------------------------------

    private static string Leaf() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>A per-test unique roster team: the cross-team filter makes
    /// every other suite's enrollments invisible to this test's works.</summary>
    private async Task<(string Team, string Slug)> FreshTeamAsync()
    {
        var slug = $"rt-w-{Leaf()}";
        await fixture.Db.SeedTeam(slug);
        return (slug, slug);
    }

    private async Task<TestDbProbe.RoutingDecisionRow> DrainAuditAsync(
        KernelTestFactory factory, string intentCommandId,
        Func<TestDbProbe.RoutingDecisionRow, TestDbProbe.RoutingDecisionRow?>? accept = null) =>
        await RoutingHarness.DrainUntilAsync(
            factory,
            async () =>
            {
                var row = await fixture.Db.RoutingDecision(intentCommandId);
                return row is null ? null : accept is null ? row : accept(row);
            },
            intentCommandId);

    private static Dictionary<string, object?> Analyse(
        string id, bool inputSchema = false, bool outputKinds = false, bool mediaTypes = false, bool contexts = true)
    {
        var descriptor = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = 1,
            ["execution"] = "asynchronous",
            ["supportsSteering"] = true,
            ["supportsCancellation"] = true,
            ["supportsNudge"] = true,
        };
        if (contexts)
        {
            descriptor["acceptedContexts"] = new[] { "project" };
        }

        if (inputSchema)
        {
            descriptor["inputSchema"] = new Dictionary<string, object?> { ["type"] = "object" };
        }

        if (outputKinds)
        {
            descriptor["outputKinds"] = new[] { "analysis.report" };
        }

        if (mediaTypes)
        {
            descriptor["outputMediaTypes"] = new[] { "text/markdown" };
        }

        return descriptor;
    }

    private static string Manifest(params Dictionary<string, object?>[] capabilities) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["apiVersion"] = "facilitator.framework/v1",
            ["kind"] = "CapabilityManifest",
            ["ownerTeam"] = "{{TEAM}}",
            ["capabilities"] = capabilities,
        });

    private async Task AdvertiseAsync(
        HttpClient kernel, string endpoint, string team, string manifest,
        bool openedBefore = false)
    {
        await fixture.Db.SeedGatewayEnrollment(endpoint, team, "isolated-executor");
        await fixture.Db.SeedCapabilityManifest(endpoint, manifest.Replace("{{TEAM}}", team));
        var openedAt = DateTime.UtcNow - (openedBefore ? TimeSpan.FromHours(2) : TimeSpan.FromSeconds(30));
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(15), openedAt);
    }

    private async Task<(Guid WorkId, long Version)> CreateWorkAsync(
        HttpClient kernel, string team, string capability, string? preference = null)
    {
        var projectId = await fixture.Db.SeedProject(team);
        var assignment = new Dictionary<string, object?>
        {
            ["capabilityId"] = capability,
            ["objective"] = "route me",
            ["targetId"] = "kernel",
            ["actorEvidenceRef"] = "identity-evidence:routing-suite",
        };
        if (preference is not null)
        {
            assignment["preferenceEndpointId"] = preference;
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["objective"] = "routing funnel",
            ["projectId"] = projectId,
            ["commandId"] = Guid.NewGuid().ToString("N"),
            ["assignments"] = new[] { assignment },
        });
        var created = await KernelOps.PostJsonAsync(kernel, "/api/works", body, HttpStatusCode.Created);
        return (created.GetProperty("workId").GetGuid(), created.GetProperty("version").GetInt64());
    }

    private static int CountAssignRows(List<string> envelopes) =>
        envelopes.Count(e => e.Contains("command.work.assign.v1", StringComparison.Ordinal));
}
