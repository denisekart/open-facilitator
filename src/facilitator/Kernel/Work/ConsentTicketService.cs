using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// Chat consent leaves a paper trail (C7, the known-soft seam): an entitled
/// chat decision is a human act with consequences, so it records THROUGH the
/// provider plane — <c>vcs.issue.create</c> plus a <c>vcs.issue.comment</c>
/// carrying decision, decider and digest — and a separate work item carries
/// that ticket. The tracker is configuration (the provider section's
/// <c>IssueRequestCapability</c>): with no tracker bound the decision still
/// records, with the person's proven identity, plus a timeline row naming the
/// missing tracker — never a silent no-op. Dispatch is outbox-only (the
/// connector is offline-tolerant by construction); a connector that does not
/// implement the capability fails the leg LOUD, which is the disclosure.
/// </summary>
public sealed class ConsentTicketService(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    ProviderRegistry providers,
    EnvelopeValidator envelopeValidator,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>The tracker-create capability the provider section opts into.</summary>
    public const string IssueCreateCapability = "vcs.issue.create";

    /// <summary>The tracker-comment capability (same as the manifest leg's).</summary>
    public const string IssueCommentCapability = "vcs.issue.comment";

    /// <summary>
    /// Record one chat consent decision. Runs AFTER the ledger settled the
    /// approval (this never decides anything) and never throws into the
    /// respond path: a tracker outage must not undo a recorded decision.
    /// </summary>
    public async Task RecordAsync(Guid approvalId, string decision, string decidedBy, CancellationToken ct)
    {
        try
        {
            await RecordCoreAsync(approvalId, decision, decidedBy, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await audit.WriteAsync(new AuditRecord(
                Actor: decidedBy,
                Action: "consent.ticket:failed",
                Resource: $"approval/{approvalId}",
                Decision: "allow",
                Reason: "ticket-leg-failed",
                EvidenceRef: null,
                PolicyVersion: "approval-v1",
                Detail: $"{{\"error\":\"{error.Message}\"}}"), ct);
        }
    }

    private async Task RecordCoreAsync(Guid approvalId, string decision, string decidedBy, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ApprovalId == approvalId, ct);
        if (approval is null)
        {
            return;
        }

        Guid? workId = approval.WorkId;
        Guid? teamId = null;
        if (workId is { } w)
        {
            teamId = await db.Works.AsNoTracking().Where(x => x.WorkId == w).Select(x => x.TeamId).FirstOrDefaultAsync(ct);
        }

        // Tracker bound = the approval's provider opts in AND a repo is
        // derivable: the work's own forge binding (channel IS the repo), or
        // the chat conversation the decision arrived on.
        var options = ResolveTracker(db, approval, workId, ct);
        // C9: an automation-opened ticket needs a HUMAN owner-account on the
        // tracker - the decided-by person's own linked account. No linked
        // account, no ticket: an ownerless ticket is exactly the accountability
        // hole C9 closes, so the decision records without the tracker leg and
        // names why.
        var ownerAccount = options is null
            ? null
            : OwnerAccountOn(db, options.Provider, decidedBy);
        if (options?.IssueRequestCapability is { Length: > 0 } capability
            && ownerAccount is { Length: > 0 } owner
            && RepoOf(db, options, workId, ct) is { Length: > 0 } repo)
        {
            await DispatchTicketAsync(db, options, capability, repo, approvalId, decision, decidedBy, owner, teamId, ct);
            return;
        }

        // No tracker: the decision stands on the person's proven identity,
        // and the missing tracker is named where a human will look.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var detail = new JsonObject
        {
            ["approvalId"] = approvalId.ToString(),
            ["decision"] = decision,
            ["decidedBy"] = decidedBy,
            ["actionDigest"] = approval.ActionDigest,
            ["tracker"] = options?.IssueRequestCapability is { Length: > 0 }
                && ownerAccount is null
                ? "owner-unlinked"
                : "missing",
        };
        if (workId is { } tracked)
        {
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = tracked,
                WorkVersion = await db.Works.Where(x => x.WorkId == tracked).Select(x => x.Version).FirstOrDefaultAsync(ct),
                Kind = "approval.decided",
                Actor = decidedBy,
                Detail = detail.ToJsonString(),
                CreatedAt = now,
            });
        }
        else
        {
            await audit.WriteAsync(new AuditRecord(
                Actor: decidedBy,
                Action: "authorization.evaluated:approval",
                Resource: $"approval/{approvalId}",
                Decision: "allow",
                Reason: ownerAccount is null && options?.IssueRequestCapability is { Length: > 0 }
                    ? "consent-recorded-tracker-owner-unlinked"
                    : "consent-recorded-without-tracker",
                EvidenceRef: null,
                PolicyVersion: "approval-v1",
                Detail: detail.ToJsonString()), ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The tracker provider: the work's bound forge when the work is
    /// bound to one, else any enabled provider that opted in (one tracker per
    /// kernel is the shape of the config; several opted-in is the operator's
    /// choice and first-match stands).</summary>
    private ProviderOptions? ResolveTracker(FacilitatorDbContext db, ApprovalEntity approval, Guid? workId, CancellationToken ct)
    {
        if (workId is { } w)
        {
            var bound = db.CommunicationBindings.AsNoTracking()
                .Where(b => b.WorkId == w)
                .Select(b => b.Provider)
                .FirstOrDefault();
            if (bound is { Length: > 0 } provider)
            {
                var own = providers.Find(provider);
                if (own?.IssueRequestCapability is { Length: > 0 })
                {
                    return own;
                }
            }
        }

        return providers.All().FirstOrDefault(o =>
            o.IssueRequestCapability is { Length: > 0 } && o.EndpointId is { Length: > 0 });
    }

    /// <summary>The tracker repo: the binding channel of a forge-bound work
    /// (forges key bindings by repo), else the opted-in provider section's
    /// chat channel is not usable — no repo, no ticket.</summary>
    private static string? RepoOf(FacilitatorDbContext db, ProviderOptions options, Guid? workId, CancellationToken ct)
    {
        if (workId is not { } w)
        {
            return null;
        }

        return db.CommunicationBindings.AsNoTracking()
            .Where(b => b.WorkId == w && b.Provider == options.Provider)
            .Select(b => b.Channel)
            .FirstOrDefault();
    }

    /// <summary>The decider's own account on the tracker provider, from the
    /// verified link (C9): `oidc:{issuer}|{subject}` is exactly the
    /// provider_identities key. Never a guess, never the bot.</summary>
    private static string? OwnerAccountOn(FacilitatorDbContext db, string provider, string decidedBy)
    {
        const string Prefix = "oidc:";
        var bar = decidedBy.IndexOf('|', StringComparison.Ordinal);
        if (!decidedBy.StartsWith(Prefix, StringComparison.Ordinal)
            || bar <= Prefix.Length || bar == decidedBy.Length - 1)
        {
            return null;
        }

        var issuer = decidedBy[Prefix.Length..bar];
        var subject = decidedBy[(bar + 1)..];
        return db.ProviderIdentities.AsNoTracking()
            .Where(i => i.Provider == provider && i.OidcIssuer == issuer && i.OidcSubject == subject)
            .Select(i => i.AccountId)
            .FirstOrDefault();
    }

    private async Task DispatchTicketAsync(
        FacilitatorDbContext db, ProviderOptions options, string capability, string repo,
        Guid approvalId, string decision, string decidedBy, string ownerProviderAccountId,
        Guid? owningTeamId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The carrier: a separate work item whose only purpose is to hold the
        // ticket (and its audit trail) apart from the consented action.
        var ticketWorkId = Guid.NewGuid();
        var team = owningTeamId
            ?? await db.Teams.AsNoTracking().Select(t => t.TeamId).FirstOrDefaultAsync(ct);
        if (team == Guid.Empty)
        {
            return;
        }

        var project = await db.Projects.FirstOrDefaultAsync(
            p => p.TeamId == team && p.Name == ProviderEventStore.ProviderProjectName, ct);
        if (project is null)
        {
            project = new ProjectEntity
            {
                ProjectId = Guid.NewGuid(),
                TeamId = team,
                Name = ProviderEventStore.ProviderProjectName,
                Config = "{}",
                CreatedAt = now,
            };
            db.Projects.Add(project);
        }

        var objective = $"Consent decision ticket {approvalId:N}: {decision}";
        db.Works.Add(new WorkEntity
        {
            WorkId = ticketWorkId,
            ProjectId = project.ProjectId,
            Objective = objective,
            State = "open",
            Version = 1,
            TeamId = team,
            ActiveWhere = new JsonObject { ["provider"] = options.Provider, ["repo"] = repo }.ToJsonString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = ticketWorkId,
            WorkVersion = 1,
            Kind = "created",
            Actor = decidedBy,
            Detail = new JsonObject
            {
                ["objective"] = objective,
                ["approvalId"] = approvalId.ToString(),
                ["decision"] = decision,
                ["decidedBy"] = decidedBy,
            }.ToJsonString(),
            CreatedAt = now,
        });

        var connector = options.IssueRequestEndpointId is { Length: > 0 } e
            ? e
            : options.Provider + "-connector";

        var createId = $"consent-ticket:{approvalId:N}";
        var create = ProviderEnvelopes.ConnectorCommand(
            ticketWorkId, capability, connector, options.Provider,
            new JsonObject
            {
                ["objective"] = objective,
                ["sourceRefs"] = new JsonArray(),
                ["repo"] = repo,
                ["title"] = objective,
                ["body"] = $"Recorded by Facilitator chat consent.\n\n- Approval: `{approvalId}`\n- Decision: {decision}\n- Decider: {decidedBy}",
                ["ownerProviderAccountId"] = ownerProviderAccountId,
                ["labels"] = new JsonArray("facilitator-consent"),
            },
            "vcs.issue",
            now,
            createId);
        AppendDispatchRow(db, create, $"link.{options.EndpointId}", createId, ticketWorkId, ct);

        var commentId = $"consent-ticket:{approvalId:N}:comment";
        var comment = ProviderEnvelopes.ConnectorCommand(
            ticketWorkId, IssueCommentCapability, connector, options.Provider,
            new JsonObject
            {
                ["repo"] = repo,
                ["issue"] = $"ticket:{approvalId:N}",
                ["body"] = $"Decision **{decision}** by {decidedBy} for action digest `{approvalActionDigest(db, approvalId, ct)}`.",
            },
            "vcs.issue.comment",
            now,
            commentId);
        AppendDispatchRow(db, comment, $"link.{options.EndpointId}", commentId, ticketWorkId, ct);

        await db.SaveChangesAsync(ct);
    }

    private static string approvalActionDigest(FacilitatorDbContext db, Guid approvalId, CancellationToken ct) =>
        db.Approvals.AsNoTracking().Where(a => a.ApprovalId == approvalId).Select(a => a.ActionDigest).FirstOrDefault()
        ?? string.Empty;

    private void AppendDispatchRow(
        FacilitatorDbContext db, JsonObject envelope, string recipient, string commandId, Guid workId, CancellationToken ct)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = recipient,
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            CommandId = commandId,
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }
}
