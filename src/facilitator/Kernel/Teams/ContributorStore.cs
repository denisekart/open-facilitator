using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Kernel.Teams;

/// <summary>One contributor as the API renders it.</summary>
public sealed record ContributorView(
    string Handle, string TeamSlug, string? DisplayName, string Remit, string Status,
    DateTime CreatedAt, DateTime? RetiredAt, string? ChangedBy, string? ChangeReason);

/// <summary>Outcome of a contributor mutation.</summary>
public enum ContributorOutcome
{
    /// <summary>The write landed.</summary>
    Stored,

    /// <summary>No team with that slug — 404.</summary>
    TeamUnknown,

    /// <summary>Handle outside <c>[a-z0-9._-]{1,64}</c> — 400.</summary>
    InvalidHandle,

    /// <summary>Remit outside <c>{"capabilityIds": [], "operations": []}</c> — 400.</summary>
    InvalidRemit,

    /// <summary>Handle owned by another team (or retired rows keep their name) — 409.</summary>
    HandleTaken,

    /// <summary>No contributor with that handle — 404.</summary>
    ContributorUnknown,
}

/// <summary>Result of a contributor mutation.</summary>
public sealed record ContributorMutation(ContributorOutcome Outcome, string? Detail)
{
    /// <summary>True when the write landed.</summary>
    public bool Stored => Outcome == ContributorOutcome.Stored;
}

/// <summary>
/// The contributor directory (K-family): named non-human process members a
/// team owns. Registering one also puts <c>contributor:&lt;handle&gt;</c> on the
/// team roster (kind <c>contributor</c>), so the contributor is a first-class
/// actor everywhere actors are consulted — never silently a human. Retirement
/// keeps the row (history names its actors, B3's law), moves the roster row
/// out of the way of nothing, and hands the contributor's open works to the
/// team (owner_actor = null + the attention view).
/// </summary>
public sealed class ContributorStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Handle grammar (the actor suffix; URL-safe everywhere).</summary>
    public static bool IsValidHandle(string handle) =>
        handle.Length is > 0 and <= 64
        && handle.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');

    /// <summary>Remit grammar: an object with string-array capabilityIds/operations.</summary>
    public static bool IsValidRemit(JsonElement remit)
    {
        if (remit.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "capabilityIds", "operations" })
        {
            if (!remit.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array
                || arr.EnumerateArray().Any(static e => e.ValueKind != JsonValueKind.String))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Contributors of one team (when <paramref name="teamSlug"/> is null: all teams an operator view needs).</summary>
    public async Task<List<ContributorView>> ListAsync(string? teamSlug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var query = db.Contributors.AsNoTracking();
        if (teamSlug is not null)
        {
            query = query.Where(c => c.Team!.Slug == teamSlug);
        }

        return await query
            .OrderBy(c => c.Handle)
            .Select(c => new ContributorView(
                c.Handle, c.Team!.Slug, c.DisplayName, c.Remit, c.Status,
                c.CreatedAt, c.RetiredAt, c.ChangedBy, c.ChangeReason))
            .ToListAsync(ct);
    }

    /// <summary>One contributor by handle (null when unknown).</summary>
    public async Task<ContributorView?> FindAsync(string handle, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Contributors.AsNoTracking()
            .Where(c => c.Handle == handle)
            .Select(c => new ContributorView(
                c.Handle, c.Team!.Slug, c.DisplayName, c.Remit, c.Status,
                c.CreatedAt, c.RetiredAt, c.ChangedBy, c.ChangeReason))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The contributor behind a canonical <c>contributor:&lt;handle&gt;</c> actor (null when not one / unknown).</summary>
    public async Task<ContributorView?> FindByActorAsync(string actor, CancellationToken ct) =>
        actor.StartsWith("contributor:", StringComparison.Ordinal)
            ? await FindAsync(actor["contributor:".Length..], ct)
            : null;

    /// <summary>Register or update one contributor (and its roster row).</summary>
    public async Task<ContributorMutation> PutAsync(
        string teamSlug, string handle, string? displayName, JsonElement remit,
        string actor, string reason, CancellationToken ct)
    {
        if (!IsValidHandle(handle))
        {
            return new ContributorMutation(ContributorOutcome.InvalidHandle,
                "handle must match [a-z0-9._-]{1,64}");
        }

        if (!IsValidRemit(remit))
        {
            return new ContributorMutation(ContributorOutcome.InvalidRemit,
                """remit must be {"capabilityIds": [], "operations": []} with string arrays""");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Slug == teamSlug, ct);
        if (team is null)
        {
            return new ContributorMutation(ContributorOutcome.TeamUnknown, null);
        }

        var other = await db.Contributors.FirstOrDefaultAsync(c => c.Handle == handle, ct);
        if (other is not null && other.TeamId != team.TeamId)
        {
            return new ContributorMutation(ContributorOutcome.HandleTaken,
                "handles are global; this one belongs to another team");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var payload = remit.GetRawText();
        if (other is null)
        {
            db.Contributors.Add(new ContributorEntity
            {
                ContributorId = Guid.NewGuid(),
                TeamId = team.TeamId,
                Handle = handle,
                DisplayName = displayName,
                Remit = payload,
                Status = "active",
                CreatedAt = now,
                ChangedBy = actor,
                ChangeReason = reason,
            });
        }
        else
        {
            other.DisplayName = displayName ?? other.DisplayName;
            other.Remit = payload;
            other.Status = "active"; // re-registration re-activates
            other.RetiredAt = null;
            other.ChangedBy = actor;
            other.ChangeReason = reason;
        }

        // One place for "who belongs": the roster row alongside.
        var rosterActor = $"contributor:{handle}";
        if (!await db.TeamMembers.AnyAsync(
                m => m.TeamId == team.TeamId && m.Actor == rosterActor, ct))
        {
            db.TeamMembers.Add(new TeamMemberEntity
            {
                TeamId = team.TeamId,
                Actor = rosterActor,
                Kind = "contributor",
                Capacity = "member",
                AddedBy = actor,
                CreatedAt = now,
            });
        }

        audit.Append(db, new AuditRecord(
            Actor: actor,
            Action: "contributors.put",
            Resource: $"contributor:{handle}",
            Decision: "allow",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "contributors-v1",
            Detail: JsonSerializer.Serialize(new { team = teamSlug, remit })));
        await db.SaveChangesAsync(ct);
        return new ContributorMutation(ContributorOutcome.Stored, null);
    }

    /// <summary>Replace the remit only (K7: an operator action, audited with author + reason + date).</summary>
    public async Task<ContributorMutation> PutRemitAsync(
        string handle, JsonElement remit, string actor, string reason, CancellationToken ct)
    {
        if (!IsValidRemit(remit))
        {
            return new ContributorMutation(ContributorOutcome.InvalidRemit,
                """remit must be {"capabilityIds": [], "operations": []} with string arrays""");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Contributors.FirstOrDefaultAsync(c => c.Handle == handle, ct);
        if (row is null)
        {
            return new ContributorMutation(ContributorOutcome.ContributorUnknown, null);
        }

        row.Remit = remit.GetRawText();
        row.ChangedBy = actor;
        row.ChangeReason = reason;
        audit.Append(db, new AuditRecord(
            Actor: actor,
            Action: "contributors.remit",
            Resource: $"contributor:{handle}",
            Decision: "allow",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "contributors-v1",
            Detail: remit.GetRawText()));
        await db.SaveChangesAsync(ct);
        return new ContributorMutation(ContributorOutcome.Stored, null);
    }

    /// <summary>Retire (K10): the row endures (history names its actors); open
    /// works lose the contributor as owner so the team attention view carries
    /// them.</summary>
    public async Task<ContributorMutation> RetireAsync(
        string handle, string actor, string reason, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Contributors.FirstOrDefaultAsync(c => c.Handle == handle, ct);
        if (row is null)
        {
            return new ContributorMutation(ContributorOutcome.ContributorUnknown, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        row.Status = "retired";
        row.RetiredAt = now;
        row.ChangedBy = actor;
        row.ChangeReason = reason;

        // K2/K10: open works move to the team, they do not die with the actor.
        var terminal = new[] { "completed", "cancelled", "failed" };
        await db.Works
            .Where(w => w.OwnerActor == $"contributor:{handle}" && !terminal.Contains(w.State))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.OwnerActor, (string?)null), ct);

        audit.Append(db, new AuditRecord(
            Actor: actor,
            Action: "contributors.retire",
            Resource: $"contributor:{handle}",
            Decision: "allow",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "contributors-v1",
            Detail: "{}"));
        await db.SaveChangesAsync(ct);
        return new ContributorMutation(ContributorOutcome.Stored, null);
    }

    /// <summary>The audit trail of one contributor (K7: remit changes name author + reason + date).</summary>
    public async Task<List<object>> HistoryAsync(string handle, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Audit.AsNoTracking()
            .Where(a => a.Resource == $"contributor:{handle}" && EF.Functions.Like(a.Action, "contributors.%"))
            .OrderByDescending(a => a.OccurredAt)
            .Take(100)
            .ToListAsync(ct);
        return rows
            .Select(a => (object)new
            {
                at = a.OccurredAt,
                action = a.Action,
                author = a.Actor,
                reason = a.Reason,
                detail = a.Detail,
            })
            .ToList();
    }

    /// <summary>N13/P9 enforcement: true when the capability is contributor-only
    /// (advertised by some contributor's remit) and the evidence names a human
    /// subject — such an assignment is refused, not silently downgraded.</summary>
    public static bool IsHumanAgainstContributorOnly(
        string? evidenceScheme, string? evidenceValue, string capabilityId, IEnumerable<ContributorView> contributors)
    {
        if (evidenceScheme is not ("kernel" or "oidc") || string.IsNullOrEmpty(evidenceValue))
        {
            return false;
        }

        // "provider:<provider>:<account>" pointers are workload claims; only
        // human-subject evidence (oidc delegated / kernel-minted human) can be
        // mistaken for a contributor.
        var namesHuman = evidenceScheme == "oidc"
            || evidenceValue.StartsWith("human:", StringComparison.Ordinal);
        if (!namesHuman)
        {
            return false;
        }

        return ContributorCapabilityIds(contributors).Contains(capabilityId, StringComparer.Ordinal);
    }

    /// <summary>Every capability id named by an active contributor's remit.</summary>
    public static IEnumerable<string> ContributorCapabilityIds(IEnumerable<ContributorView> contributors) =>
        contributors
            .Where(c => c.Status == "active")
            .SelectMany(c =>
            {
                try
                {
                    return JsonDocument.Parse(c.Remit).RootElement
                        .GetProperty("capabilityIds").EnumerateArray()
                        .Select(static e => e.GetString() ?? string.Empty);
                }
                catch (JsonException)
                {
                    return Enumerable.Empty<string>();
                }
            })
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.Ordinal);
}
