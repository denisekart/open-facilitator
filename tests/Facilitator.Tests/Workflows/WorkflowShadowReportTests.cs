using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Workflows;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Workflows;

/// <summary>
/// The shadow reconciliation report (<see cref="WorkflowShadowReport"/>):
/// the plane's would-do (shadow step-log rows) diffed against the arm's
/// ACTUAL effects, keyed on the delivery identity copied inside the
/// trigger row's envelope — never a timing join. Legs pinned: no-plane-run
/// says so rather than inventing a match; a deferred (non-shadow) run
/// reconciles vacuously; a guard miss vs. a forking arm lands a decision
/// diff; a fully-matching fork (objective, successor link, binding
/// repoint, successor reply row + command-id shape + sentence modulo the
/// successor id) diffs nothing; a partially-executed arm names every
/// missing field; ledger rows without a delivery id and trigger rows of
/// other providers/deliveries never produce rows.
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowShadowReportTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Provider = "shadowtest";
    private static readonly DateTime Base = new(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly KernelTestFactory _factory = new(fixture, noDispatcher: true);
    private readonly List<Guid> _workIds = [];
    private readonly List<Guid> _bindingIds = [];
    private readonly List<Guid> _linkIds = [];
    private readonly List<Guid> _outboxIds = [];
    private readonly List<string> _defIds = [];
    private readonly string _deliveryPrefix = "shd-" + Guid.NewGuid().ToString("N")[..8];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ = _factory.CreateAuthedClient(); // boots migrations
        await CleanupAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await CleanupAsync();
        _factory.Dispose();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task NoPlaneRunIsReportedNotInvented()
    {
        var delivery = NextDelivery();
        var trigger = await SeedTriggerAsync(delivery, subjectWork: null, withLogs: false);
        await SeedLedgerAsync(delivery, "steered:noop");

        var rows = await ReportAsync();
        var row = Assert.Single(rows);
        Assert.False(row.PlaneWouldAct);
        Assert.False(row.ArmActed);
        Assert.False(row.Match);
        Assert.Contains(row.Diffs, d => d.StartsWith("no-plane-run:", StringComparison.Ordinal));
        Assert.Equal(trigger, row.InstanceId);
    }

    [Fact]
    public async Task DeferredRunWithoutShadowRowsReconcilesVacuously()
    {
        var delivery = NextDelivery();
        var instance = await SeedTriggerAsync(delivery, subjectWork: null, withLogs: true, shadow: false);
        await SeedLedgerAsync(delivery, "work/" + Guid.NewGuid() + ":input");

        var row = Assert.Single(await ReportAsync());
        Assert.True(row.ArmActed);
        Assert.False(row.PlaneWouldAct);
        Assert.True(row.Match);
        Assert.Empty(row.Diffs);
        Assert.Equal(instance, row.InstanceId);
    }

    [Fact]
    public async Task GuardMissAgainstNonForkingArmMatches_AndAgainstForkingArmDiffsDecision()
    {
        // Plane: guard did not hit and no fork planned; arm: no fork.
        var quiet = NextDelivery();
        await SeedTriggerAsync(quiet, subjectWork: null, withLogs: true, shadow: true, guardHit: false, planFork: false);
        await SeedLedgerAsync(quiet, "steered:quiet");

        // Plane: guard hit + fork planned; arm: no fork ⇒ decision diff.
        var loud = NextDelivery();
        await SeedTriggerAsync(loud, subjectWork: null, withLogs: true, shadow: true, guardHit: true, planFork: true);
        await SeedLedgerAsync(loud, "review:action");

        var rows = await ReportAsync();
        Assert.Equal(2, rows.Count);
        var quietRow = rows.Single(r => r.DeliveryId == quiet);
        Assert.True(quietRow.Match);
        var loudRow = rows.Single(r => r.DeliveryId == loud);
        Assert.False(loudRow.Match);
        // As-built: the diff line renders booleans with Boolean.ToString
        // (capitalised True/False).
        Assert.Contains(loudRow.Diffs, d => d.StartsWith("decision: plane-would-act=True arm-acted=False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullyExecutedForkReconcilesWithZeroDiffs()
    {
        var delivery = NextDelivery();
        var team = await SeedTeamAsync();
        var (subject, successor) = await SeedWorksAsync(team);
        var defId = DefId();
        var plan = await SeedTriggerAsync(delivery, subject, withLogs: true, shadow: true, guardHit: true, planFork: true, defId: defId);
        _ = plan;

        // The arm's actual effects for this delivery: successor work with
        // the same objective, the successor link on the review's subject,
        // the binding repointed, and the successor reply row.
        await SeedWorkLinkAsync(subject, successor, "successor", "seed");
        await SeedBindingAsync(successor);
        var replyText = $"Work {subject:N} is closed - this continues as work {successor:N}";
        await SeedOutboxAsync($"successor:{successor:N}",
            JsonSerializer.Serialize(new JsonObject { ["data"] = new JsonObject { ["text"] = replyText } }));
        await SeedLedgerAsync(delivery, $"work/{successor}:input");

        var row = Assert.Single(await ReportAsync());
        Assert.True(row.PlaneWouldAct);
        Assert.True(row.ArmActed);
        Assert.True(row.Match);
        Assert.Empty(row.Diffs);
    }

    [Fact]
    public async Task PartiallyExecutedArmNamesEveryDisagreeingField()
    {
        var delivery = NextDelivery();
        var team = await SeedTeamAsync();
        var (subject, successor) = await SeedWorksAsync(team, objective: "the arm's own objective");

        // The plane planned a different objective, a different link note, a
        // malformed reply command id — and the arm left the binding and the
        // reply row undone.
        var defId = DefId();
        await SeedTriggerAsync(delivery, subject, withLogs: true, shadow: true, guardHit: true, planFork: true,
            defId: defId, planObjective: "the plane's objective", planLinkNote: "seed",
            planReplyCommandId: "successor:short");
        await SeedWorkLinkAsync(subject, successor, "successor", "arm-note");
        await SeedLedgerAsync(delivery, $"work/{successor}:input");

        var row = Assert.Single(await ReportAsync());
        Assert.False(row.Match);
        Assert.Contains(row.Diffs, d => d.StartsWith("objective: plane=the plane's objective arm=the arm's own objective", StringComparison.Ordinal));
        Assert.Contains(row.Diffs, d => d.StartsWith("link: plane=successor/seed arm=successor/arm-note", StringComparison.Ordinal));
        Assert.Contains(row.Diffs, d => d.StartsWith("binding:", StringComparison.Ordinal));
        Assert.Contains(row.Diffs, d => d.StartsWith("reply: arm appended no successor reply row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplyCommandIdShapeIsCheckedEvenWhenTheSentenceAgrees()
    {
        var delivery = NextDelivery();
        var team = await SeedTeamAsync();
        var (subject, successor) = await SeedWorksAsync(team);
        await SeedTriggerAsync(delivery, subject, withLogs: true, shadow: true, guardHit: true, planFork: true,
            planReplyCommandId: "successor:XYZ"); // wrong shape: not 32 lower hex
        await SeedWorkLinkAsync(subject, successor, "successor", "seed");
        await SeedBindingAsync(successor);
        await SeedOutboxAsync($"successor:{successor:N}", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["text"] = $"Work {subject:N} is closed - this continues as work {successor:N}",
            },
        }.ToJsonString());
        await SeedLedgerAsync(delivery, $"work/{successor}:input");

        var row = Assert.Single(await ReportAsync());
        Assert.False(row.Match);
        Assert.Contains(row.Diffs, d => d.StartsWith("reply-command-id: plane shape wrong: successor:XYZ", StringComparison.Ordinal));
        Assert.DoesNotContain(row.Diffs, d => d.StartsWith("reply-text:", StringComparison.Ordinal));
        Assert.DoesNotContain(row.Diffs, d => d.StartsWith("binding:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LedgerRowWithoutDeliveryIdAndForeignTriggerAreSkipped()
    {
        // Ledger row with a judgment but no delivery identity ⇒ skipped.
        await using (var db = await ContextFactory().CreateDbContextAsync())
        {
            db.ProviderEvents.Add(new ProviderEventEntity
            {
                ProviderEventId = Guid.NewGuid(),
                Provider = Provider,
                ProviderDeliveryId = null,
                BodySha256 = new string('a', 64),
                Body = "{}",
                VerifiedReference = "steered:noop",
                ReceivedAt = Base,
            });
            await db.SaveChangesAsync();
        }

        // A trigger row for a DIFFERENT delivery never joins the ledger row.
        var mine = NextDelivery();
        await SeedTriggerAsync(mine, subjectWork: null, withLogs: false);
        var other = await SeedTriggerAsync("shd-other-" + Guid.NewGuid().ToString("N")[..8],
            subjectWork: null, withLogs: false);
        _ = other;
        await SeedLedgerAsync(mine, "steered:noop");

        var rows = await ReportAsync();
        Assert.Single(rows);
    }

    // ---- plumbing ----------------------------------------------------------

    private string NextDelivery() => _deliveryPrefix + "-" + Guid.NewGuid().ToString("N")[..6];

    private string DefId()
    {
        var id = "t.shadow-" + Guid.NewGuid().ToString("N")[..8];
        _defIds.Add(id);
        return id;
    }

    private async Task<IReadOnlyList<WorkflowShadowReport.Row>> ReportAsync()
    {
        var report = _factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<WorkflowShadowReport>();
        var rows = await report.ReportAsync(Base - TimeSpan.FromMinutes(30), default);
        // Foreign suites share this container; only this test's deliveries
        // are the subject.
        return rows.Where(r => r.DeliveryId.StartsWith(_deliveryPrefix, StringComparison.Ordinal)).ToList();
    }

    private IDbContextFactory<FacilitatorDbContext> ContextFactory() =>
        _factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>();

    /// <summary>Seed the plane's trigger row + (optionally) the instance and
    /// its step log; returns the deterministic instance id.</summary>
    private async Task<Guid> SeedTriggerAsync(
        string delivery,
        Guid? subjectWork,
        bool withLogs,
        bool shadow = true,
        bool guardHit = true,
        bool planFork = true,
        string? defId = null,
        string planObjective = "shared objective",
        string planLinkKind = "successor",
        string planLinkNote = "seed",
        string? planReplyCommandId = null)
    {
        defId ??= DefId();
        var triggerRowId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var instance = WorkflowIdentity.InstanceIdFor(defId, 1, triggerRowId);
        var envelope = JsonSerializer.Serialize(new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = "urn:facilitator:instance:main",
            ["type"] = WorkflowShadowReport.ReviewType,
            ["time"] = "2026-04-01T12:00:00Z",
            ["data"] = new JsonObject
            {
                ["provider"] = Provider,
                ["providerDeliveryId"] = delivery,
            },
        });
        var planeSuccessor = WorkflowIdentity.ForkWorkId(instance, "fork");
        var replyId = planReplyCommandId ?? $"successor:{planeSuccessor:N}";
        await using (var db = await ContextFactory().CreateDbContextAsync())
        {
            db.Outbox.Add(new OutboxEntity
            {
                OutboxId = outboxId,
                RecipientEndpointId = WorkflowTrigger.Recipient,
                AggregateType = "work",
                AggregateId = subjectWork,
                EventType = WorkflowShadowReport.ReviewType,
                CommandId = WorkflowIdentity.TriggerCommandId(defId, 1, triggerRowId),
                Envelope = envelope,
                WorkId = subjectWork,
                CreatedAt = Base,
                AvailableAt = Base,
            });
            if (withLogs)
            {
                db.WorkflowInstances.Add(new WorkflowInstanceEntity
                {
                    InstanceId = instance,
                    DefId = defId,
                    DefVersion = 1,
                    Status = shadow ? "shadowed" : "completed",
                    StartedAt = Base,
                    UpdatedAt = Base,
                    InvocationPath = defId,
                });
                db.WorkflowStepLog.Add(new WorkflowStepLogEntity
                {
                    InstanceId = instance,
                    Seq = 1,
                    StepId = "guard-terminal",
                    Status = shadow ? "shadow" : "done",
                    Output = guardHit ? """{"hit":true}""" : """{"hit":false}""",
                    StartedAt = Base,
                    UpdatedAt = Base,
                });
                if (planFork)
                {
                    var replyText = subjectWork is { } subject
                        ? $"Work {subject:N} is closed - this continues as work {planeSuccessor:N}"
                        : "Work 00000000-0000-0000-0000-000000000000 is closed - this continues as work 11111111-1111-1111-1111-111111111111";
                    db.WorkflowStepLog.Add(new WorkflowStepLogEntity
                    {
                        InstanceId = instance,
                        Seq = 2,
                        StepId = "fork",
                        Status = shadow ? "shadow" : "done",
                        Output = JsonSerializer.Serialize(new JsonObject
                        {
                            ["wouldDo"] = new JsonObject
                            {
                                ["objective"] = planObjective,
                                ["linkKind"] = planLinkKind,
                                ["linkNote"] = planLinkNote,
                                ["replyCommandId"] = replyId,
                                ["replyText"] = replyText,
                            },
                        }),
                        StartedAt = Base,
                        UpdatedAt = Base,
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        return instance;
    }

    private async Task SeedLedgerAsync(string delivery, string judgment)
    {
        await using var db = await ContextFactory().CreateDbContextAsync();
        db.ProviderEvents.Add(new ProviderEventEntity
        {
            ProviderEventId = Guid.NewGuid(),
            Provider = Provider,
            ProviderDeliveryId = delivery,
            BodySha256 = new string('b', 64),
            Body = "{}",
            VerifiedReference = judgment,
            ReceivedAt = Base,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedTeamAsync()
    {
        var slug = "shadow-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedTeam(slug);
        return slug;
    }

    private async Task<(Guid Subject, Guid Successor)> SeedWorksAsync(string teamSlug, string? objective = null)
    {
        await using var db = await ContextFactory().CreateDbContextAsync();
        var teamId = await db.Teams.Where(t => t.Slug == teamSlug).Select(t => t.TeamId).SingleAsync();
        var subject = new WorkEntity
        {
            WorkId = Guid.NewGuid(),
            Objective = "the review's subject",
            State = "completed",
            Version = 1,
            TeamId = teamId,
            CreatedAt = Base,
            UpdatedAt = Base,
        };
        var successor = new WorkEntity
        {
            WorkId = Guid.NewGuid(),
            Objective = objective ?? "shared objective",
            State = "active",
            Version = 1,
            TeamId = teamId,
            CreatedAt = Base,
            UpdatedAt = Base,
        };
        db.Works.AddRange(subject, successor);
        await db.SaveChangesAsync();
        _workIds.AddRange([subject.WorkId, successor.WorkId]);
        return (subject.WorkId, successor.WorkId);
    }

    private async Task SeedWorkLinkAsync(Guid workId, Guid refWorkId, string kind, string note)
    {
        await using var db = await ContextFactory().CreateDbContextAsync();
        var link = new WorkLinkEntity
        {
            LinkId = Guid.NewGuid(),
            WorkId = workId,
            RefWorkId = refWorkId,
            Kind = kind,
            Note = note,
            CreatedAt = Base,
        };
        db.WorkLinks.Add(link);
        await db.SaveChangesAsync();
        _linkIds.Add(link.LinkId);
    }

    private async Task SeedBindingAsync(Guid workId)
    {
        await using var db = await ContextFactory().CreateDbContextAsync();
        var binding = new CommunicationBindingEntity
        {
            BindingId = Guid.NewGuid(),
            Provider = Provider,
            Channel = "C" + Guid.NewGuid().ToString("N")[..6],
            RootMessageId = "R" + Guid.NewGuid().ToString("N")[..6],
            WorkId = workId,
            CreatedAt = Base,
        };
        db.CommunicationBindings.Add(binding);
        await db.SaveChangesAsync();
        _bindingIds.Add(binding.BindingId);
    }

    private async Task SeedOutboxAsync(string commandId, string envelope)
    {
        await using var db = await ContextFactory().CreateDbContextAsync();
        var outboxId = Guid.NewGuid();
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = outboxId,
            RecipientEndpointId = "link.shadow",
            AggregateType = "work",
            EventType = "org.facilitator.command.communication.reply.v1",
            CommandId = commandId,
            Envelope = envelope,
            CreatedAt = Base,
            AvailableAt = Base,
        });
        await db.SaveChangesAsync();
        _outboxIds.Add(outboxId);
    }

    private async Task CleanupAsync()
    {
        if (_defIds.Count > 0)
        {
            var defs = string.Join(",", _defIds.Select(d => $"'{d}'"));
            await fixture.Db.NonQueryAsync(
                $"DELETE FROM facilitator.workflow_step_log WHERE instance_id IN "
                + $"(SELECT id FROM facilitator.workflow_instances WHERE def_id IN ({defs}))", _ => { });
            await fixture.Db.NonQueryAsync(
                $"DELETE FROM facilitator.workflow_instances WHERE def_id IN ({defs})", _ => { });
            await fixture.Db.NonQueryAsync(
                $"DELETE FROM facilitator.workflow_definitions WHERE def_id IN ({defs})", _ => { });
        }

        if (_outboxIds.Count > 0)
        {
            await fixture.Db.NonQueryAsync(
                "DELETE FROM facilitator.outbox WHERE outbox_id = ANY($1)",
                cmd => cmd.Parameters.AddWithValue(_outboxIds.ToArray()));
        }

        // Positional $n binds: AddWithValue is positional, and a literal
        // `@p…` in the SQL text reads as a named parameter to Npgsql.
        await fixture.Db.NonQueryAsync(
            "DELETE FROM facilitator.provider_events WHERE provider = $1 AND provider_delivery_id LIKE $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(Provider);
                cmd.Parameters.AddWithValue(_deliveryPrefix + "%");
            });

        if (_linkIds.Count > 0)
        {
            await fixture.Db.NonQueryAsync(
                "DELETE FROM facilitator.work_links WHERE link_id = ANY($1)",
                cmd => cmd.Parameters.AddWithValue(_linkIds.ToArray()));
        }

        if (_bindingIds.Count > 0)
        {
            await fixture.Db.NonQueryAsync(
                "DELETE FROM facilitator.communication_bindings WHERE binding_id = ANY($1)",
                cmd => cmd.Parameters.AddWithValue(_bindingIds.ToArray()));
        }

        if (_workIds.Count > 0)
        {
            await fixture.Db.NonQueryAsync(
                "DELETE FROM facilitator.works WHERE work_id = ANY($1)",
                cmd => cmd.Parameters.AddWithValue(_workIds.ToArray()));
        }
    }
}
