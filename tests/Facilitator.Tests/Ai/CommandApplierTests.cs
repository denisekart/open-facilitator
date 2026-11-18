using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel;
using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The command-application plane driven straight at the applier — no provider
/// ingress, no decode, no dispatcher. Every verb and every rejection path is
/// asserted through its judgment string, the one validator-gated outbox row it
/// writes, and the <c>ai_turns</c> ledger pair. <see cref="CommandApplier"/>
/// takes a live <see cref="FacilitatorDbContext"/> by design (it joins the
/// caller's transaction), so the suite is DB-backed and borrows the applier and
/// the context factory from the host's own DI graph: real validator, real
/// metrics, provider options built in-test. Only the optimistic-concurrency
/// branch of /cancel (which needs a second concurrent writer) is left to the
/// pipeline suites.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class CommandApplierTests : IAsyncLifetime, IDisposable
{
    public CommandApplierTests(DispatchDbFixture fixture) => _fixture = fixture;

    private static readonly DateTime Now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AiPolicy Policy = new(true, "balanced", "model", null, "auto", 600);

    private readonly DispatchDbFixture _fixture;

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task UnknownVerbRepliesTheHelpPointerAndLedgersBothDirections()
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync("/frobnicate the thing");

        Assert.Equal("cmd:unknown", effect.Judgment);
        Assert.Null(effect.WorkId);
        Assert.Null(effect.PostCommit);
        var envelope = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:frobnicate:{plane.DeliveryId}");
        Assert.NotNull(envelope);
        Assert.Contains("Unknown command /frobnicate", envelope);
        Assert.Contains("Type /help", envelope);
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "slash"));
    }

    [Fact]
    public async Task HelpListsEveryRegistryVerbIncludingMergedCustomOnes()
    {
        var plane = await PlaneAsync("""{"commands":[{"verb":"run-ci-tests","description":"Run CI & tests","capabilityId":"ci.run","args":[]}]}""");

        var effect = await plane.ApplyAsync("/help");

        Assert.Equal("cmd:help", effect.Judgment);
        var envelope = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:help:{plane.DeliveryId}");
        Assert.Contains("/work", envelope);
        Assert.Contains("/approve", envelope);
        Assert.Contains("/run-ci-tests", envelope);
        Assert.Contains("Run CI & tests", envelope); // descriptions are html-decoded
    }

    [Fact]
    public async Task LinkVerbPointsAtTheCodePathAndCreatesNoWork()
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync("/link");

        Assert.Equal("cmd:link", effect.Judgment);
        Assert.Null(effect.WorkId);
        Assert.Contains("mention /link to get a code",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:link:{plane.DeliveryId}"));
        Assert.Null(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
    }

    [Theory]
    [InlineData("reaction")]
    [InlineData("reply")]
    public async Task WorkVerbCreatesWorkBindingProgressAndRoutingIntent(string cadence)
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync("/work build the thing", replyVsReaction: cadence);

        var workId = Assert.NotNull(effect.WorkId);
        Assert.Equal($"work/{workId}", effect.Judgment);
        Assert.Equal(("open", 1L), await _fixture.Db.WorkStateRow(workId));
        Assert.Equal(workId, await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "created"));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "slash"));

        var ack = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:work:{plane.DeliveryId}");
        Assert.NotNull(ack);
        if (cadence == "reply")
        {
            Assert.Contains($"Work {workId.ToString("N")[..8]} created.", ack);
            Assert.Contains("communication.reply", ack);
        }
        else
        {
            Assert.Contains(ProviderEventStore.AckEmoji, ack);
            Assert.Contains("communication.react", ack);
        }

        // Provider project find-or-create + the progress event row.
        Assert.Contains("work created (mattermost command)",
            await plane.OutboxEnvelopeOf(workId, "org.facilitator.event.work.progress.v1") ?? string.Empty);
        // The provider's default capability seeds the routing intent.
        Assert.Contains("analysis.summarize",
            await _fixture.Db.OutboxEnvelopeByCommand($"routing:work-assign:{workId:N}") ?? string.Empty);
    }

    [Fact]
    public async Task CustomVerbSeedsItsOwnCapabilityAndRendersDeclaredArgsIntoTheObjective()
    {
        var plane = await PlaneAsync("""{"commands":[{"verb":"run-ci-tests","description":"Run CI","capabilityId":"ci.run","args":[{"name":"branch","required":false}]}]}""");

        var effect = await plane.ApplyAsync("/run-ci-tests main extra words");

        var workId = Assert.NotNull(effect.WorkId);
        Assert.Equal("extra words [branch=main]", plane.ObjectiveOf(workId));
        Assert.Contains("ci.run",
            await _fixture.Db.OutboxEnvelopeByCommand($"routing:work-assign:{workId:N}") ?? string.Empty);
    }

    [Fact]
    public async Task AWorkCreatingVerbOnARosteredRuleTeamMovesTheWorkThere()
    {
        var plane = await PlaneAsync();
        var otherTeam = "applier-route-" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.Db.SeedTeam(otherTeam);

        var effect = await plane.ApplyAsync("/work routed", rule: new AssignRule { Team = otherTeam });

        var workId = Assert.NotNull(effect.WorkId);
        Assert.Equal(otherTeam, plane.TeamSlugOf(workId));
        // Routed works ride the coding command-id base.
        Assert.Contains("work-assign-coding",
            await _fixture.Db.OutboxEnvelopeByCommand($"routing:work-assign-coding:{workId:N}") ?? string.Empty);
    }

    [Fact]
    public async Task AnUnrosteredRuleTeamFailsOpenToTheWorkloadsOwnTeam()
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync("/work typo team", rule: new AssignRule { Team = "no-such-team" });

        Assert.Equal(plane.TeamSlug, plane.TeamSlugOf(Assert.NotNull(effect.WorkId)));
    }

    [Fact]
    public async Task VerbatimFallbackCreatesWorkWithTheNoHintLine()
    {
        var plane = await PlaneAsync();

        var effect = await plane.VerbatimAsync("just do it");

        var workId = Assert.NotNull(effect.WorkId);
        Assert.Equal($"work/{workId}", effect.Judgment);
        Assert.Equal(workId, await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        var ack = await _fixture.Db.OutboxEnvelopeByCommand($"mention:{plane.DeliveryId}:ack");
        Assert.NotNull(ack);
        Assert.Contains("On it.", ack);
        Assert.Contains(string.Format(CommandApplier.SlashPlaneHintFormat, "bot-1"), ack);
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    [Theory]
    [InlineData("/status", "No Work is bound to this conversation")]
    [InlineData("/status 11111111-1111-1111-1111-111111111111", "No Work with that id.")]
    public async Task StatusWithoutAResolvableWorkSaysWhich(string text, string expected)
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync(text);

        Assert.Equal("cmd:status:nobound", effect.Judgment);
        Assert.Contains(expected, await _fixture.Db.OutboxEnvelopeByCommand($"cmd:status:{plane.DeliveryId}"));
    }

    [Fact]
    public async Task StatusOfABoundWorkWithoutExtrasAnswersStateAndVersionOnly()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: true);

        await plane.ApplyAsync("/status");

        var envelope = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:status:{plane.DeliveryId}");
        Assert.Contains($"state open, version 1.", envelope);
        Assert.DoesNotContain("awaiting consent", envelope);
        Assert.DoesNotContain("link(s)", envelope);
        Assert.DoesNotContain("cost reported", envelope);
    }

    [Fact]
    public async Task StatusReportsHoldsLinksCostAndTheLastEvent()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("active", bind: true);
        await _fixture.Db.SeedWorkLink(workId, await plane.SeedWorkAsync("open", bind: false), "successor");
        var heldApproval = Guid.NewGuid();
        await _fixture.Db.SeedApproval(heldApproval, workId);
        await using (var db = await plane.ContextAsync())
        {
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = 2,
                Kind = "manifest.recorded",
                Actor = "link:gw-1",
                Detail = "{}",
                CreatedAt = Now,
            });
            db.HeldActions.Add(new HeldActionEntity
            {
                HeldActionId = Guid.NewGuid(),
                ApprovalId = heldApproval,
                WorkId = workId,
                CapabilityId = "ci.run",
                EndpointId = plane.EndpointId,
                Envelope = "{}",
                ActionDigest = "sha256:" + new string('a', 64),
                State = "pending",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.Works.Where(w => w.WorkId == workId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Cost, "{\"usd\":12}"));
            await db.SaveChangesAsync();
        }

        var effect = await plane.ApplyAsync("/status");

        Assert.StartsWith($"cmd:status:work/{workId}", effect.Judgment);
        var envelope = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:status:{plane.DeliveryId}");
        Assert.Contains($"Work {workId.ToString("N")[..8]}: state active, version 1", envelope);
        Assert.Contains("1 action(s) awaiting consent", envelope);
        Assert.Contains("1 link(s)", envelope);
        Assert.Contains("cost reported", envelope);
        Assert.Contains("last event manifest.recorded at 2026-05-01 12:00 UTC", envelope);
    }

    [Fact]
    public async Task CancelWithoutAResolvableWorkIsNothingToCancel()
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync("/cancel");

        Assert.Equal("cmd:cancel:nobound", effect.Judgment);
        Assert.Contains("nothing to cancel", await _fixture.Db.OutboxEnvelopeByCommand($"cmd:cancel:{plane.DeliveryId}"));
    }

    [Fact]
    public async Task CancelByAnUnmappedProviderAccountIsDeniedAndLeavesTheWork()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: true);

        var effect = await plane.ApplyAsync("/cancel");

        Assert.Equal("cmd:denied", effect.Judgment);
        Assert.Contains("Link via /link", await _fixture.Db.OutboxEnvelopeByCommand($"cmd:cancel:{plane.DeliveryId}"));
        Assert.Equal("open", (await _fixture.Db.WorkStateRow(workId))?.State);
    }

    [Fact]
    public async Task CancelOfAMappedAccountWithNoProvenWorkWriteIsDenied()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: true);
        await _fixture.Db.SeedProviderIdentity("mattermost", "demo-team", plane.Actor, plane.Issuer, plane.Subject);

        var effect = await plane.ApplyAsync("/cancel");

        Assert.Equal("cmd:denied", effect.Judgment);
        Assert.Equal("open", (await _fixture.Db.WorkStateRow(workId))?.State);
    }

    [Fact]
    public async Task CancelInAnIllegalStateReportsTheTransitionRejection()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("completed", bind: true);
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync("/cancel");

        Assert.Equal("cmd:cancel:illegal", effect.Judgment);
        Assert.Contains("cannot cancel in state completed",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:cancel:{plane.DeliveryId}"));
        Assert.Equal("completed", (await _fixture.Db.WorkStateRow(workId))?.State);
    }

    [Fact]
    public async Task CancelTransitionsVersionFencedAndLedgersTheTransition()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: true);
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync("/cancel");

        Assert.Equal($"cmd:cancel:work/{workId}", effect.Judgment);
        Assert.Equal(("cancelled", 2L), await _fixture.Db.WorkStateRow(workId));
        Assert.Equal(1, await _fixture.Db.TimelineEventCount(workId, "state.transition"));
        Assert.Contains($"Work {workId.ToString("N")[..8]} cancelled.",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:cancel:{plane.DeliveryId}"));
        Assert.Contains("open→cancelled",
            await plane.OutboxEnvelopeOf(workId, "org.facilitator.event.work.progress.v1") ?? string.Empty);
    }

    [Fact]
    public async Task CancelByWorkIdNeedsNoConversationBinding()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: false);
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync($"/cancel {workId:N}");

        Assert.Equal($"cmd:cancel:work/{workId}", effect.Judgment);
        Assert.Equal("cancelled", (await _fixture.Db.WorkStateRow(workId))?.State);
    }

    [Theory]
    [InlineData("/approve", "Usage: /approve <approval-id> <accept|reject>.")]
    [InlineData("/approve accept", "Usage: /approve <approval-id> <accept|reject>.")] // a decision where the id belongs
    [InlineData("/approve not-a-guid accept", "'not-a-guid' is not an approval id (a guid).")]
    public async Task ApproveOutsideItsUsageAnswersTheUsageLine(string text, string expected)
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync(text);

        Assert.Equal("cmd:approve:usage", effect.Judgment);
        Assert.Null(effect.PostCommit);
        Assert.Contains(expected,
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}"));
    }

    [Fact]
    public async Task ApproveByAnUnmappedAccountIsDeniedWithoutAPostCommitLeg()
    {
        var plane = await PlaneAsync();

        var effect = await plane.ApplyAsync($"/approve {Guid.NewGuid():N} accept");

        Assert.Equal("cmd:denied", effect.Judgment);
        Assert.Null(effect.PostCommit);
        Assert.Contains("linked Facilitator identity to decide approvals",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}"));
    }

    [Fact]
    public async Task ApproveAcksInTransactionAndDecidesTheTicketPostCommit()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: false);
        var approvalId = Guid.NewGuid();
        await _fixture.Db.SeedApproval(approvalId, workId, eligibleActorsJson: $$"""["oidc:{{plane.Issuer}}|{{plane.Subject}}"]""");
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync($"/approve {approvalId:N} accept");

        // In-transaction: only the react ack; the ledger says nothing yet.
        Assert.Equal($"cmd:approve:{approvalId}", effect.Judgment);
        Assert.Null(effect.WorkId);
        Assert.NotNull(effect.PostCommit);
        Assert.Contains("communication.react",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}"));
        Assert.Equal("pending", (await _fixture.Db.Approval(approvalId))?.State);

        await effect.PostCommit!(plane.Host.Services.CreateScope().ServiceProvider);

        Assert.Equal("approved", (await _fixture.Db.Approval(approvalId))?.State);
        var result = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}:result");
        Assert.NotNull(result);
        Assert.Contains($"Approval {approvalId.ToString("N")[..8]}: approved.", result);
    }

    [Fact]
    public async Task DenyIsApproveRejectOnTheSameLeg()
    {
        var plane = await PlaneAsync();
        var workId = await plane.SeedWorkAsync("open", bind: false);
        var approvalId = Guid.NewGuid();
        await _fixture.Db.SeedApproval(approvalId, workId, eligibleActorsJson: $$"""["oidc:{{plane.Issuer}}|{{plane.Subject}}"]""");
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync($"/deny {approvalId:N}");
        await effect.PostCommit!(plane.Host.Services.CreateScope().ServiceProvider);

        Assert.Equal($"cmd:approve:{approvalId}", effect.Judgment);
        Assert.Equal("denied", (await _fixture.Db.Approval(approvalId))?.State);
        Assert.Contains("denied.",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}:result") ?? string.Empty);
    }

    [Fact]
    public async Task ApproveOfAMissingTicketDisclosesTheFailureOnTheResultLine()
    {
        var plane = await PlaneAsync();
        await plane.LinkActorAsync();

        var effect = await plane.ApplyAsync($"/approve {Guid.NewGuid():N} reject");
        await effect.PostCommit!(plane.Host.Services.CreateScope().ServiceProvider);

        Assert.Contains("could not be recorded",
            await _fixture.Db.OutboxEnvelopeByCommand($"cmd:approve:{plane.DeliveryId}:result") ?? string.Empty);
    }

    [Fact]
    public async Task AppendCommandRowRejectsAnEnvelopeTheValidatorRefuses()
    {
        // The single gate for every outbound row of this plane: a kernel-built
        // envelope that is not wire-valid is a kernel bug and must throw.
        var plane = await PlaneAsync();
        await using var db = await plane.ContextAsync();
        var notAnEnvelope = new JsonObject { ["type"] = "org.facilitator.command.nonsense.v1" };

        Assert.Throws<InvalidOperationException>(() =>
            plane.Applier.AppendCommandRow(db, notAnEnvelope, plane.Options, Now, workId: null));
    }

    [Fact]
    public async Task AppendCommandRowWithoutTheProviderFailsForWantOfDefaultAddressing()
    {
        var plane = await PlaneAsync();
        await using var db = await plane.ContextAsync();
        var envelope = ProviderEnvelopes.Progress(Guid.NewGuid(), "kernel-built row", Now);

        Assert.Throws<InvalidOperationException>(() =>
        {
            plane.Applier.AppendCommandRow(db, envelope, options: null, Now, workId: null, recipient: null);
        });
    }

    [Fact]
    public async Task AppendCommandRowUsesAnExplicitRecipientAndCommandId()
    {
        var plane = await PlaneAsync();
        await using var db = await plane.ContextAsync();
        var envelope = ProviderEnvelopes.Progress(Guid.NewGuid(), "kernel-built row", Now);

        plane.Applier.AppendCommandRow(db, envelope, plane.Options, Now, workId: null,
            recipient: "link.explicit", commandId: "explicit-command");
        await db.SaveChangesAsync();

        Assert.Equal(1, await _fixture.Db.OutboxCountByCommand("explicit-command"));
        Assert.Equal(1, await _fixture.Db.OutboxCountForRecipient("link.explicit"));
    }

    // ---- plumbing ------------------------------------------------------------

    private sealed class PlaneSpec
    {
        private readonly CommandApplierTests owner;

        private readonly Guid teamId;

        public PlaneSpec(
            CommandApplierTests owner,
            KernelTestFactory host,
            CommandRegistry registry,
            string teamSlug,
            Guid teamId,
            string endpointId,
            string channel,
            string root,
            string deliveryId,
            string issuer,
            string subject)
        {
            this.owner = owner;
            Host = host;
            Applier = host.Services.GetRequiredService<CommandApplier>();
            Registry = registry;
            TeamSlug = teamSlug;
            this.teamId = teamId;
            EndpointId = endpointId;
            Channel = channel;
            Root = root;
            DeliveryId = deliveryId;
            Issuer = issuer;
            Subject = subject;
            Key = AiLedgerStore.ProviderKey("mattermost", channel, root);
            Options = new ProviderOptions
            {
                Provider = "mattermost",
                Enabled = true,
                EndpointId = endpointId,
                Tenant = "demo-team",
                BotUserId = "bot-1",
            };
            Reference = new ProviderEventStore.Reference("mattermost", "demo-team", channel, root, deliveryId, "thread");
        }

        public KernelTestFactory Host { get; }

        public CommandApplier Applier { get; }

        public CommandRegistry Registry { get; }

        public string TeamSlug { get; }

        public string EndpointId { get; }

        public string Channel { get; }

        public string Root { get; }

        public string DeliveryId { get; }

        public string Issuer { get; }

        public string Subject { get; }

        public string Actor { get; } = "acct-" + Guid.NewGuid().ToString("N");

        public string Key { get; }

        public ProviderEventStore.Reference Reference { get; }

        public ProviderOptions Options { get; }

        public Task<FacilitatorDbContext> ContextAsync() =>
            Host.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync();

        public async Task<CommandEffect> ApplyAsync(
            string text, string? replyVsReaction = null, AssignRule? rule = null)
        {
            var parsed = CommandParser.TryParse(text, Registry)!;
            await using var db = await ContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            var effect = await Applier.ApplyAsync(
                db, Now, parsed, Registry, Reference, Options, Actor, teamId, DeliveryId,
                replyVsReaction ?? "reaction", Policy, CancellationToken.None, rule: rule);
            await db.SaveChangesAsync();
            await tx.CommitAsync(); // the arm's/sink's law: effects commit with the ledger
            return effect;
        }

        public async Task<CommandEffect> VerbatimAsync(string text)
        {
            await using var db = await ContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            var effect = await Applier.VerbatimWorkAsync(
                db, Now, text, Reference, Options, Actor, teamId, DeliveryId, "reply", CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return effect;
        }

        /// <summary>The stored envelope of one effect row of this plane (by
        /// work + event type, since kernel-built rows carry a fresh command id).</summary>
        public async Task<string?> OutboxEnvelopeOf(Guid workId, string eventType)
        {
            await using var db = await ContextAsync();
            return await db.Outbox.AsNoTracking()
                .Where(o => o.WorkId == workId && o.EventType == eventType)
                .Select(o => o.Envelope).FirstOrDefaultAsync();
        }

        public Task LinkActorAsync() => owner.LinkActorAsync(this);

        public async Task<Guid> SeedWorkAsync(string state, bool bind)
        {
            var projectId = await owner._fixture.Db.SeedProject(TeamSlug);
            var workId = await owner._fixture.Db.SeedWorkOwned(projectId, ownerActor: null, state, TeamSlug);
            if (bind)
            {
                await owner._fixture.Db.SeedBinding("mattermost", Channel, Root, workId, $"seed-{DeliveryId}");
            }

            return workId;
        }

        public string ObjectiveOf(Guid workId)
        {
            using var db = ContextAsync().GetAwaiter().GetResult();
            return db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => w.Objective).Single();
        }

        public string? TeamSlugOf(Guid workId)
        {
            using var db = ContextAsync().GetAwaiter().GetResult();
            return db.Works.AsNoTracking().Where(w => w.WorkId == workId)
                .Select(w => w.Team!.Slug).SingleOrDefault();
        }
    }

    private async Task<PlaneSpec> PlaneAsync(string? commandsConfig = null)
    {
        var endpoint = "gw-applier-" + Guid.NewGuid().ToString("N")[..8];
        var factory = new KernelTestFactory(_fixture, noDispatcher: true);
        _factories.Add(factory);
        factory.CreateClient().Dispose(); // force host start => migrations

        var registry = CommandRegistry.BuiltIns;
        if (commandsConfig is not null)
        {
            using var doc = JsonDocument.Parse(commandsConfig);
            registry = CommandRegistry.Merge(registry, doc.RootElement.Clone());
        }

        var teamSlug = "applier-team-" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.Db.SeedTeam(teamSlug);
        Guid teamId;
        await using (var db = await factory.Services
                         .GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            teamId = await db.Teams.AsNoTracking().Where(t => t.Slug == teamSlug).Select(t => t.TeamId).SingleAsync();
        }

        return new PlaneSpec(this, factory, registry, teamSlug, teamId, endpoint,
            $"chan-{Guid.NewGuid():N}", $"root-{Guid.NewGuid():N}", $"post-{Guid.NewGuid():N}",
            "seed-issuer", "subj-" + Guid.NewGuid().ToString("N"));
    }

    private async Task LinkActorAsync(PlaneSpec plane)
    {
        await _fixture.Db.SeedProviderIdentity("mattermost", "demo-team", plane.Actor, plane.Issuer, plane.Subject);
        await using var db = await plane.ContextAsync();
        db.Audit.Add(new AuditEventEntity
        {
            AuditId = Guid.NewGuid(),
            OccurredAt = DateTime.UtcNow,
            Actor = $"oidc:{plane.Issuer}|{plane.Subject}",
            Action = "authorization.evaluated:work.write",
            Resource = "/api/works",
            Decision = "allow",
            Reason = "seed",
            Detail = "{}",
        });
        await db.SaveChangesAsync();
    }
}
