using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The outcome of applying one command: the judgment string recorded on the
/// provider_events ledger, the Work touched/created (when any), and an
/// optional post-commit action (approval respond rides its own transaction,
/// the steer precedent).
/// </summary>
/// <param name="Judgment">Machine judgment, e.g. <c>cmd:work</c>, <c>cmd:cancel:illegal</c>.</param>
/// <param name="WorkId">Work created or acted on, when any.</param>
/// <param name="PostCommit">Runs after the caller's transaction commits (null = nothing pending).</param>
public sealed record CommandEffect(
    string Judgment,
    Guid? WorkId,
    Func<IServiceProvider, Task>? PostCommit = null);

/// <summary>
/// Executes a <see cref="ParsedCommand"/> against the kernel — the ONE effect
/// plane shared by the deterministic slash path (provider arm, synchronous)
/// and the AI decode sink (the model may only select a registry verb; the
/// applier decides what it does). Every effect answers the conversation via
/// the reply/react envelope law and appends the <c>ai_turns</c> ledger rows in
/// the caller's transaction; effects needing a second transaction (approval
/// respond) return a <see cref="CommandEffect.PostCommit"/> for the arm
/// dispatcher to run after commit.
/// </summary>
public sealed class CommandApplier(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    KernelMetrics metrics)
{
    /// <summary>Reply line appended to every AI-degraded verbatim ack: the
    /// slash plane works with zero AI, and the human should know it.</summary>
    public const string SlashPlaneHintFormat = "Commands: /help {0} works without AI.";

    /// <summary>
    /// Apply one parsed command (built-in or custom verb) in the caller's
    /// context/transaction. Custom verbs behave exactly like <c>/work</c> with
    /// the registry entry's capability seed.
    /// </summary>
    /// <param name="db">Caller's context (the arm's or the sink's transaction).</param>
    /// <param name="now">Decision timestamp (caller's clock).</param>
    /// <param name="cmd">Parser (or decoder) output; <see cref="ParsedCommand.Unknown"/> gets the polite /help pointer.</param>
    /// <param name="registry">Merged registry the verb was resolved against.</param>
    /// <param name="reference">Conversation reference of the triggering message.</param>
    /// <param name="options">Owning provider options (addressing + capability default).</param>
    /// <param name="actorProviderAccountId">Provider account that sent the command.</param>
    /// <param name="teamGuid">Workload team that owns created Work (the link principal's team).</param>
    /// <param name="deliveryId">Provider delivery id — the idempotency spine of every row.</param>
    /// <param name="replyVsReaction">Ack style for Work-creating verbs (the <c>notifications</c> row).</param>
    /// <param name="policy">Current AiDeck policy (reserved for composed acks; slash effects are deterministic).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="ledgerBasis">Ledger basis for the appended turns: <c>slash</c> from the arm, <c>model</c> when a decode result drove the apply.</param>
    public async Task<CommandEffect> ApplyAsync(
        FacilitatorDbContext db,
        DateTime now,
        ParsedCommand cmd,
        CommandRegistry registry,
        ProviderEventStore.Reference reference,
        ProviderOptions options,
        string actorProviderAccountId,
        Guid teamGuid,
        string deliveryId,
        string replyVsReaction,
        AiPolicy policy,
        CancellationToken ct,
        string ledgerBasis = "slash",
        AssignRule? rule = null,
        JsonArray? sourceRefs = null)
    {
        registry.TryGet(cmd.Verb, out var spec);
        metrics.AiDecode(cmd.Unknown ? "cmd-unknown" : $"cmd-{cmd.Verb}");
        AppendTurn(db, reference, actorProviderAccountId, "in", LedgerText(cmd), now, ledgerBasis);
        var key = $"cmd:{cmd.Verb}:{deliveryId}";
        var subject = $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}";

        if (cmd.Unknown)
        {
            return await ReplyAsync(db, reference, options, key, subject,
                $"Unknown command /{cmd.Verb}. Type /help to see what I can do.",
                $"cmd:unknown", ledgerBasis, now);
        }

        return cmd.Verb switch
        {
            "help" => await ReplyAsync(db, reference, options, key, subject,
                RenderHelp(registry), "cmd:help", ledgerBasis, now),
            "status" => await StatusAsync(db, reference, options, cmd, key, subject, ledgerBasis, now, ct),
            "cancel" => await CancelAsync(db, reference, options, cmd, actorProviderAccountId, key, subject, ledgerBasis, now, ct),
            "approve" => await ApproveAsync(db, reference, options, cmd, actorProviderAccountId, key, subject, deliveryId, ledgerBasis, now, ct),
            "link" => await ReplyAsync(db, reference, options, key, subject,
                "Linking runs through /link — mention /link to get a code.", "cmd:link", ledgerBasis, now),
            // /work and every custom verb: Work creation on the provider plane.
            _ => await CreateWorkAsync(db, reference, options, spec, cmd, actorProviderAccountId,
                teamGuid, deliveryId, replyVsReaction, ledgerBasis, now, ct, rule, sourceRefs),
        };
    }

    /// <summary>
    /// The verbatim Work-creation fallback (AI degraded / AiDeck inert): the
    /// exact Work block the mention arm creates today, plus the low-noise ack
    /// with the <see cref="SlashPlaneHintFormat"/> line. Shared by the arm's
    /// inert path and the decode sink's fallback — one honest degradation
    /// path, ledgered with basis <c>fallback</c>.
    /// </summary>
    public async Task<CommandEffect> VerbatimWorkAsync(
        FacilitatorDbContext db,
        DateTime now,
        string text,
        ProviderEventStore.Reference reference,
        ProviderOptions options,
        string actor,
        Guid teamGuid,
        string deliveryId,
        string replyVsReaction,
        CancellationToken ct,
        AssignRule? rule = null,
        JsonArray? sourceRefs = null)
    {
        _ = ct;
        metrics.AiDecode("fallback");
        AppendTurn(db, reference, actor, "in", Trim(text), now, "fallback");
        var workId = await CreateWorkBlockAsync(db, reference, options, capabilityId: null,
            objective: Trim(text), actor, teamGuid, deliveryId, now, ct, rule, sourceRefs);
        var hint = string.Format(SlashPlaneHintFormat, options.BotUserId ?? "here");
        var ack = replyVsReaction == "reply"
            ? ProviderEventStore.ReplyEnvelope(ProviderEventStore.TargetOf(reference),
                $"On it.\n{hint}", $"mention:{deliveryId}:ack",
                $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now)
            : ProviderEventStore.ReactEnvelope(ProviderEventStore.TargetOf(reference),
                ProviderEventStore.AckEmoji, $"mention:{deliveryId}:ack",
                $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now);
        AppendCommandRow(db, ack, options, now, workId: workId);
        AppendTurn(db, reference, actor, "out", $"On it.\n{hint}", now, "fallback");
        return new CommandEffect($"work/{workId}", workId);
    }

    /// <summary>Append one validated outbox row (the arm's AppendCommand column
    /// set): validator-gated, default reply addressing to the provider's link
    /// endpoint. Public so the decode sink writes its reply rows through the
    /// exact same gate.</summary>
    public void AppendCommandRow(
        FacilitatorDbContext db,
        JsonObject envelope,
        ProviderOptions? options,
        DateTime now,
        Guid? workId,
        string? recipient = null,
        string? commandId = null)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = commandId ?? envelope["data"]?["idempotencyKey"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            RecipientEndpointId = recipient ?? $"link.{(options ?? throw new InvalidOperationException("AppendCommandRow needs the owning provider for default addressing")).EndpointId}",
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    // ---- verbs --------------------------------------------------------------

    private async Task<CommandEffect> StatusAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        ParsedCommand cmd, string key, string subject, string basis, DateTime now, CancellationToken ct)
    {
        var resolved = await ResolveWorkAsync(db, reference, cmd, ct);
        if (resolved is null)
        {
            return await ReplyAsync(db, reference, options, key, subject,
                cmd.Args.TryGetValue("work-id", out _)
                    ? "No Work with that id."
                    : "No Work is bound to this conversation yet — mention me with a task or use /work <objective>.",
                "cmd:status:nobound", basis, now);
        }

        var (workId, state, version) = resolved.Value;
        var last = await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == workId)
            .OrderByDescending(e => e.WorkVersion).ThenByDescending(e => e.CreatedAt)
            .Select(e => new { e.Kind, e.CreatedAt })
            .FirstOrDefaultAsync(ct);
        var text = $"Work {Short(workId)}: state {state}, version {version}"
            + (last is null ? "." : $", last event {last.Kind} at {last.CreatedAt:yyyy-MM-dd HH:mm} UTC.");
        return await ReplyAsync(db, reference, options, key, subject, text, $"cmd:status:work/{workId}", basis, now);
    }

    private async Task<CommandEffect> CancelAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        ParsedCommand cmd, string actor, string key, string subject, string basis, DateTime now, CancellationToken ct)
    {
        var resolved = await ResolveWorkAsync(db, reference, cmd, ct);
        if (resolved is null)
        {
            return await ReplyAsync(db, reference, options, key, subject,
                cmd.Args.TryGetValue("work-id", out _)
                    ? "No Work with that id."
                    : "No Work is bound to this conversation — nothing to cancel.",
                "cmd:cancel:nobound", basis, now);
        }

        var (workId, state, version) = resolved.Value;
        if (!await CanCancelAsync(db, reference, actor, ct))
        {
            return await ReplyAsync(db, reference, options, key, subject,
                "You need a linked Facilitator identity with work permissions to cancel. Link via /link.",
                "cmd:denied", basis, now);
        }

        if (!StateGuard.TryParseWork(state, out var from) || !StateGuard.CanTransition(from, WorkState.Cancelled))
        {
            return await ReplyAsync(db, reference, options, key, subject,
                $"Work {Short(workId)}: cannot cancel in state {state}.", "cmd:cancel:illegal", basis, now);
        }

        // Version-fenced direct transition (the EfCoordinationStore.ApplyTransitionAsync
        // guard inlined): the arm's tx holds no lock on the row, the fence is
        // the race guard.
        var bumped = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE facilitator.works
                SET state = {"cancelled"},
                    version = version + 1,
                    updated_at = {now}
              WHERE work_id = {workId} AND version = {version}
             """, ct);
        if (bumped == 0)
        {
            return await ReplyAsync(db, reference, options, key, subject,
                $"Work {Short(workId)} changed underneath — cancel did not apply, send it again.",
                "cmd:cancel:conflict", basis, now);
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version + 1,
            Kind = "state.transition",
            Actor = $"provider:{reference.Provider}:{actor}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["from"] = state,
                ["to"] = "cancelled",
                ["reason"] = "/cancel",
            }),
            CreatedAt = now,
        });
        AppendCommandRow(db, ProviderEnvelopes.Progress(workId, $"work {state}→cancelled", now), options, now, workId);
        return await ReplyAsync(db, reference, options, key, subject,
            $"Work {Short(workId)} cancelled.", $"cmd:cancel:work/{workId}", basis, now);
    }

    private async Task<CommandEffect> ApproveAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        ParsedCommand cmd, string actor, string key, string subject, string deliveryId, string basis,
        DateTime now, CancellationToken ct)
    {
        var approvalText = cmd.Args.TryGetValue("approval-id", out var id) ? id : null;
        var choice = cmd.Args.TryGetValue("decision", out var d) ? d.ToLowerInvariant() : null;
        if (approvalText is null || choice is not ("accept" or "reject"))
        {
            return await ReplyAsync(db, reference, options, key, subject,
                "Usage: /approve <approval-id> <accept|reject>.", "cmd:approve:usage", basis, now);
        }

        if (!Guid.TryParse(approvalText, out var approvalId))
        {
            return await ReplyAsync(db, reference, options, key, subject,
                $"'{approvalText}' is not an approval id (a guid).", "cmd:approve:usage", basis, now);
        }

        var delegated = await DelegatedActorAsync(db, reference, actor, ct);
        if (delegated is null)
        {
            return await ReplyAsync(db, reference, options, key, subject,
                "You need a linked Facilitator identity to decide approvals. Link via /link.",
                "cmd:denied", basis, now);
        }

        // The approval ledger runs its own transaction (law D4 delegated
        // identity): acknowledge in-transaction, answer with the outcome
        // post-commit — the steer precedent.
        AppendCommandRow(db, ProviderEventStore.ReactEnvelope(
            ProviderEventStore.TargetOf(reference), ProviderEventStore.AckEmoji,
            key, subject, now), options, now, workId: null);
        var decision = choice == "accept" ? "approved" : "denied";
        return new CommandEffect($"cmd:approve:{approvalId}", null, sp =>
            RespondApprovalAsync(sp, reference, options, approvalId, decision, delegated, actor, deliveryId, now, ct));
    }

    private async Task RespondApprovalAsync(
        IServiceProvider sp, ProviderEventStore.Reference reference, ProviderOptions options,
        Guid approvalId, string decision, string delegatedActor, string providerActor,
        string deliveryId, DateTime now, CancellationToken ct)
    {
        var ingress = sp.GetRequiredService<Facilitator.Kernel.Work.ApprovalIngress>();
        var root = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["approvalId"] = approvalId.ToString(),
                ["decision"] = decision,
                ["actorEvidenceRef"] = $"provider:{reference.Provider}:{providerActor}",
            },
        };
        using var doc = JsonDocument.Parse(root.ToJsonString());
        var arm = await ingress.HandleRespondAsync(doc.RootElement, delegatedActor, null, null, mintEvidence: false, ct);
        var text = arm.Refused
            ? $"Approval {Short(approvalId)}: could not be recorded ({arm.Problem?.Detail ?? arm.Status})."
            : $"Approval {Short(approvalId)}: {decision}.";
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        AppendCommandRow(db, ProviderEventStore.ReplyEnvelope(
            ProviderEventStore.TargetOf(reference), text,
            $"cmd:approve:{deliveryId}:result",
            $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now), options, now, workId: null);
        AiLedgerStore.Append(db, new AiTurnAppend(
            AiLedgerStore.ProviderKey(reference.Provider, reference.Channel, reference.Root),
            Actor: delegatedActor, ProviderAccountId: providerActor, Direction: "out", Text: text, Basis: "slash"), now);
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, arm.ApprovalId, ct);
    }

    private async Task<CommandEffect> CreateWorkAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        CommandSpec? spec, ParsedCommand cmd, string actor, Guid teamGuid, string deliveryId,
        string replyVsReaction, string basis, DateTime now, CancellationToken ct,
        AssignRule? rule = null, JsonArray? sourceRefs = null)
    {
        var objective = Trim(Objective(cmd));
        var workId = await CreateWorkBlockAsync(db, reference, options,
            capabilityId: spec?.CapabilityId is { Length: > 0 } cap ? cap : options.AssignCapabilityId,
            objective, actor, teamGuid, deliveryId, now, ct, rule, sourceRefs);
        var ack = replyVsReaction == "reply"
            ? $"On it. Work {Short(workId)} created."
            : "On it.";
        var envelope = replyVsReaction == "reply"
            ? ProviderEventStore.ReplyEnvelope(ProviderEventStore.TargetOf(reference), ack,
                $"cmd:{cmd.Verb}:{deliveryId}", $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now)
            : ProviderEventStore.ReactEnvelope(ProviderEventStore.TargetOf(reference), ProviderEventStore.AckEmoji,
                $"cmd:{cmd.Verb}:{deliveryId}", $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now);
        AppendCommandRow(db, envelope, options, now, workId);
        AppendTurn(db, reference, actor, "out", ack, now, basis);
        return new CommandEffect($"work/{workId}", workId);
    }

    // ---- shared blocks -------------------------------------------------------

    /// <summary>The Work-creation block (the mention arm's, verbatim):
    /// find-or-create provider project, Work open/v1, created event, progress
    /// row, conversation binding, routing intent.</summary>
    private async Task<Guid> CreateWorkBlockAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        string? capabilityId, string objective, string actor, Guid teamGuid, string deliveryId,
        DateTime now, CancellationToken ct,
        AssignRule? rule = null, JsonArray? sourceRefs = null)
    {
        // A chat-label rule may name the OWNING team (the endpoint's fleet):
        // the manifest ledger team-scopes a work's artifacts, so the work
        // lives on the routed team or its worker could never record a
        // manifest. Unrostered team ⇒ the workload's own team (fail-open: a
        // provider config typo must not drop the Work).
        var teamId = teamGuid;
        if (rule?.Team is { Length: > 0 } ruleTeamSlug
            && await db.Teams.AsNoTracking().Where(t => t.Slug == ruleTeamSlug)
                .Select(t => t.TeamId).FirstOrDefaultAsync(ct) is { } rostered && rostered != default)
        {
            teamId = rostered;
        }

        var workId = Guid.NewGuid();
        var project = await db.Projects.FirstOrDefaultAsync(
            p => p.TeamId == teamId && p.Name == ProviderEventStore.ProviderProjectName, ct);
        if (project is null)
        {
            project = new ProjectEntity
            {
                ProjectId = Guid.NewGuid(),
                TeamId = teamId,
                Name = ProviderEventStore.ProviderProjectName,
                Config = "{}",
                CreatedAt = now,
            };
            db.Projects.Add(project);
        }

        db.Works.Add(new WorkEntity
        {
            WorkId = workId,
            ProjectId = project.ProjectId,
            Objective = objective,
            State = "open",
            Version = 1,
            TeamId = teamId,
            ActiveWhere = ProviderEventStore.ActiveWhereOf(reference),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = 1,
            Kind = "created",
            Actor = $"link:{reference.Provider}:{actor}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["objective"] = objective }),
            CreatedAt = now,
        });
        AppendCommandRow(db, ProviderEnvelopes.Progress(workId, $"work created ({reference.Provider} command)", now),
            options, now, workId);
        db.CommunicationBindings.Add(new CommunicationBindingEntity
        {
            BindingId = Guid.NewGuid(),
            Provider = reference.Provider,
            Channel = reference.Channel,
            RootMessageId = reference.Root,
            ParentMessageId = reference.MessageId,
            WorkId = workId,
            IdempotencyKey = deliveryId,
            CreatedAt = now,
        });

        if (sourceRefs is { Count: > 0 })
        {
            // Inbound media of the creating message: auditable timeline row,
            // refs verbatim (frozen artifactRef items).
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = 1,
                Kind = "provider.attachment",
                Actor = $"provider:{reference.Provider}",
                Detail = new JsonObject
                {
                    ["sourceRefs"] = (JsonNode)sourceRefs.DeepClone(),
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        var seedCapability = capabilityId is { Length: > 0 } c ? c : options.AssignCapabilityId;
        var routedCapability = rule?.CapabilityId is { Length: > 0 } ruleCapability
            ? ruleCapability
            : seedCapability;
        if (routedCapability is { Length: > 0 })
        {
            var teamSlug = await db.Teams.AsNoTracking().Where(t => t.TeamId == teamId)
                .Select(t => t.Slug).FirstOrDefaultAsync(ct);
            // A matched chat rule contributes chat-domain re-hydration inputs
            // (deliberately NOT the forge repo/issue pair — the coding worker
            // reads those keys for forge coordinates) and rides the coding
            // command-id base, exactly like the issue arm's routing intent.
            JsonObject? extraInputs = rule is null
                ? null
                : new JsonObject
                {
                    ["chat"] = reference.Channel,
                    ["topic"] = reference.Root,
                };
            var intent = new RoutingIntent(
                workId,
                routedCapability,
                teamSlug,
                PreferenceEndpointId: rule?.EndpointId is { Length: > 0 } ruleEndpoint ? ruleEndpoint : options.AssignEndpointId,
                TargetId: rule?.TargetId ?? options.AssignTargetId ?? options.Provider + "-interim",
                CommandIdBase: rule is null ? "work-assign" : "work-assign-coding",
                EffectKey: workId.ToString("N"),
                Objective: objective,
                SourceRefsJson: sourceRefs?.ToJsonString() ?? "[]",
                ExtraInputsJson: extraInputs?.ToJsonString(),
                ActorEvidenceRef: $"kernel:provider-ingest:{workId}");
            db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
        }

        return workId;
    }

    private async Task<CommandEffect> ReplyAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ProviderOptions options,
        string key, string subject, string text, string judgment, string basis, DateTime now)
    {
        AppendCommandRow(db, ProviderEventStore.ReplyEnvelope(
            ProviderEventStore.TargetOf(reference), text, key, subject, now), options, now, workId: null);
        AppendTurn(db, reference, null, "out", text, now, basis);
        return await Task.FromResult(new CommandEffect(judgment, null));
    }

    /// <summary>Bound Work (or the given work-id): id, state, version.</summary>
    private static async Task<(Guid WorkId, string State, long Version)?> ResolveWorkAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, ParsedCommand cmd, CancellationToken ct)
    {
        Guid? workId = cmd.Args.TryGetValue("work-id", out var given) && Guid.TryParse(given, out var parsed)
            ? parsed
            : null;
        if (workId is null)
        {
            workId = await db.CommunicationBindings.AsNoTracking()
                .Where(b => b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root)
                .Select(b => b.WorkId)
                .FirstOrDefaultAsync(ct);
        }

        if (workId is not { } id)
        {
            return null;
        }

        var row = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == id)
            .Select(w => new { w.WorkId, w.State, w.Version })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : ((Guid)row.WorkId, row.State, row.Version);
    }

    /// <summary>
    /// The <c>work.write</c> gate for a provider actor: the request must map
    /// (provider_identities) to a canonical oidc actor that the role gate has
    /// allowed for <c>work.write</c> at least once on the REST plane (the
    /// <c>authorization.evaluated:work.write</c> audit allow is the existing
    /// binding read path — the kernel stores no per-actor role table beyond
    /// claim rules, so a proven allow is the evidence). Unmapped or never
    /// allowed ⇒ deny (fail closed).
    /// </summary>
    private static async Task<bool> CanCancelAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, string actor, CancellationToken ct)
    {
        var actorKey = await DelegatedActorAsync(db, reference, actor, ct);
        return actorKey is not null && await db.Audit.AsNoTracking().AnyAsync(a =>
            a.Action == "authorization.evaluated:work.write" && a.Decision == "allow" && a.Actor == actorKey, ct);
    }

    /// <summary>Canonical <c>oidc:iss|sub</c> for a provider account, or null
    /// when the account is unmapped.</summary>
    private static async Task<string?> DelegatedActorAsync(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, string actor, CancellationToken ct)
    {
        var identity = await db.ProviderIdentities.AsNoTracking().FirstOrDefaultAsync(i =>
            i.Provider == reference.Provider && i.Tenant == reference.Tenant && i.AccountId == actor, ct);
        return identity is null ? null : $"oidc:{identity.OidcIssuer}|{identity.OidcSubject}";
    }

    /// <summary>Objective: the remainder plus rendered declared args (custom
    /// verbs carry their values as args, not free text).</summary>
    private static string Objective(ParsedCommand cmd)
    {
        if (cmd.Args.Count == 0)
        {
            return cmd.Rest;
        }

        var rendered = string.Join(", ", cmd.Args.Select(a => $"{a.Key}={a.Value}"));
        return cmd.Rest.Length == 0 ? rendered : $"{cmd.Rest} [{rendered}]";
    }

    private static string RenderHelp(CommandRegistry registry) =>
        "Available commands:\n"
        + string.Join("\n", registry.All().Select(s => $"/{s.Verb} — {System.Net.WebUtility.HtmlDecode(s.Description)}"));

    private static void AppendTurn(
        FacilitatorDbContext db, ProviderEventStore.Reference reference, string? outActorOverride,
        string direction, string text, DateTime now, string basis) =>
        AiLedgerStore.Append(db, new AiTurnAppend(
            AiLedgerStore.ProviderKey(reference.Provider, reference.Channel, reference.Root),
            Actor: null,
            ProviderAccountId: outActorOverride,
            Direction: direction,
            Text: text,
            Basis: basis), now);

    private static string LedgerText(ParsedCommand cmd) =>
        $"/{cmd.Verb}" + (cmd.Args.Count == 0 ? " " : " " + string.Join(' ', cmd.Args.Select(a => $"{a.Key}={a.Value}")))
        + (cmd.Rest.Length == 0 ? string.Empty : " | " + cmd.Rest);

    private static string Trim(string text) => text.Length <= 2000 ? text : text[..2000];

    private static string Short(Guid id) => id.ToString("N")[..8];
}
