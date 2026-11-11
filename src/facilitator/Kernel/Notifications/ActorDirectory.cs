using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Notifications;

/// <summary>Outcome of resolving one human name to a roster actor (B5):
/// zero, exactly one, or an ambiguity that MUST be asked — a silent pick is
/// a defect (N4).</summary>
public sealed record ActorResolution(IReadOnlyList<string> Candidates)
{
    public bool Resolved => Candidates.Count == 1;

    public bool Ambiguous => Candidates.Count > 1;
}

/// <summary>
/// Name → canonical-actor resolution across the roster (B5). The name
/// vocabulary is what a team actually says: the provider account names its
/// linked people carry (the proven mapping <c>provider_identities</c>
/// records), and the roster's <c>contributor:&lt;handle&gt;</c> actors.
/// Exact matches win; a unique prefix match resolves; anything else returns
/// EVERY candidate so the caller can ask — never a silent pick.
/// </summary>
public sealed class ActorDirectory
{
    /// <summary>Resolve one name within one team (null team = every team the
    /// kernel knows, matches still disclosed as a list).</summary>
    public static async Task<ActorResolution> ResolveAsync(
        FacilitatorDbContext db, string? teamSlug, string name, CancellationToken ct)
    {
        var needle = name.Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return new ActorResolution([]);
        }

        // Roster actors of the scoped team (humans by canonical oidc actor,
        // contributors by handle).
        var members = await (from m in db.TeamMembers.AsNoTracking()
                             where teamSlug == null || m.Team!.Slug == teamSlug
                             select new { m.Actor, m.Kind }).ToListAsync(ct);
        var rosterActors = members.Select(m => m.Actor).ToHashSet(StringComparer.Ordinal);

        // Name surface 1: linked provider accounts (the link flow proved
        // account ↔ subject, so the account name is a name the team uses).
        var byAccount = await (from i in db.ProviderIdentities.AsNoTracking()
                               select new { Name = i.AccountId.ToLower(), i.OidcIssuer, i.OidcSubject })
            .ToListAsync(ct);
        var humans = byAccount
            .Where(i => rosterActors.Contains($"oidc:{i.OidcIssuer}|{i.OidcSubject}"))
            .Select(i => ($"oidc:{i.OidcIssuer}|{i.OidcSubject}", i.Name))
            .ToList();

        // Name surface 2: contributor handles.
        var contributors = rosterActors
            .Where(a => a.StartsWith("contributor:", StringComparison.Ordinal))
            .Select(a => (a, a["contributor:".Length..].ToLowerInvariant()))
            .ToList();

        var named = humans.Concat(contributors).ToList();
        var exact = named.Where(n => n.Item2 == needle).Select(n => n.Item1)
            .Distinct(StringComparer.Ordinal).ToList();
        if (exact.Count != 0)
        {
            return new ActorResolution(exact);
        }

        var prefix = named.Where(n => n.Item2.StartsWith(needle, StringComparison.Ordinal)).Select(n => n.Item1)
            .Distinct(StringComparer.Ordinal).ToList();
        return new ActorResolution(prefix);
    }

    /// <summary>The disambiguation ask (B5): a reply listing every match so a
    /// human picks — the question IS the delivery.</summary>
    public static string DisambiguationText(string name, IReadOnlyList<string> candidates) =>
        $"'{name}' matches more than one team member: {string.Join(", ", Describe(candidates))}. "
        + "Which one did you mean?";

    /// <summary>Human-readable candidate labels (handles read better than
    /// subject guids).</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<string> candidates) =>
        candidates.Select(c => c.StartsWith("contributor:", StringComparison.Ordinal)
            ? c["contributor:".Length..]
            : c[(c.LastIndexOf('|') + 1)..]);
}
