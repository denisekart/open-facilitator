using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Teams;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Notifications;

/// <summary>A rung the egress resolver may take: a conversation target with
/// the link endpoint that can deliver it.</summary>
public sealed record EgressRung(ConversationTarget Target, string EndpointId);

/// <summary>Outcome of resolving an egress ladder: the chosen rung, or why
/// nothing can deliver (P5: the abandonment reason is a fact, never silence).</summary>
public sealed record EgressAnswer(EgressRung? Rung, string? Reason);

/// <summary>
/// The egress ladder (B4, F5): bound conversation → the team's channel for
/// that provider → an anonymized public post in the team channel (P5: the
/// fallback is public, so it anonymizes) → undeliverable with a reason.
/// A DM rung requires a LINKED provider identity (P4) — never a guessed
/// account id; an unlinked DM is skipped, not attempted.
/// </summary>
public sealed class EgressResolver(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    ProviderRegistry providers)
{
    /// <summary>Resolve the delivering rung for one ledger row (the row's own
    /// target when it names one — that IS the bound-conversation rung — else
    /// the work's binding, else the team's public channel).</summary>
    public async Task<EgressAnswer> ResolveAsync(NotificationEntity row, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Rung order comes from the row's ladder (the emitter knows where the
        // request came from); each rung is checked against reality here.
        foreach (var rung in Ladder(row))
        {
            if (await DeliverableAsync(db, rung, row.Actor, ct) is { } endpoint)
            {
                return new EgressAnswer(new EgressRung(rung, endpoint), null);
            }
        }

        // The team's public channel for any enabled chat provider: the last
        // rung before silence, anonymized by the emitter's text.
        var teamSlug = row.TeamId is { } teamId
            ? await db.Teams.AsNoTracking().Where(t => t.TeamId == teamId).Select(t => t.Slug).FirstOrDefaultAsync(ct)
            : null;
        if (teamSlug is { Length: > 0 } slug
            && await TeamChannelAsync(db, slug, ct) is { } fallback)
        {
            return new EgressAnswer(fallback, "fell back to the team channel");
        }

        return new EgressAnswer(null, row.Actor is null
            ? "no channel reaches this audience"
            : "no linked identity and no channel reaches this actor");
    }

    /// <summary>The row's declared ladder, target first (the emitter always
    /// puts the most-specific address first).</summary>
    internal static List<ConversationTarget> Ladder(NotificationEntity row)
    {
        var ladder = new List<ConversationTarget>();
        Push(ladder, row.Target);
        try
        {
            foreach (var node in JsonNode.Parse(row.Fallback)?.AsArray() ?? [])
            {
                Push(ladder, node?.ToJsonString() ?? "{}");
            }
        }
        catch (JsonException)
        {
            // A malformed ladder is just a shorter ladder.
        }

        return ladder;

        static void Push(List<ConversationTarget> targets, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("provider", out var provider)
                    && provider.ValueKind == JsonValueKind.String
                    && doc.RootElement.TryGetProperty("channelOrResourceId", out var channel)
                    && channel.ValueKind == JsonValueKind.String)
                {
                    targets.Add(Target(doc.RootElement));
                }
            }
            catch (JsonException)
            {
                // skip a rung that is not a target
            }
        }
    }

    private static ConversationTarget Target(JsonElement data) =>
        new(
            data.GetProperty("provider").GetString()!,
            data.TryGetProperty("tenantOrOrganizationId", out var t) ? t.GetString() : null,
            data.GetProperty("channelOrResourceId").GetString()!,
            data.TryGetProperty("rootThreadId", out var r) ? r.GetString() : null,
            data.TryGetProperty("parentMessageId", out var pm) ? pm.GetString() : null,
            data.TryGetProperty("subjectRef", out var sr) ? sr.GetString() : null,
            data.GetProperty("conversationKind").GetString()!,
            data.TryGetProperty("actorProviderAccountId", out var a) ? a.GetString() : null);

    /// <summary>The endpoint able to deliver this target, null when none is:
    /// the provider must be enabled, and a DM must be backed by a linked
    /// provider identity (P4 — an unlinked DM never gets a guessed account).</summary>
    private async Task<string?> DeliverableAsync(FacilitatorDbContext db, ConversationTarget target, string? actor, CancellationToken ct)
    {
        if (providers.Find(target.Provider) is not { EndpointId: { Length: > 0 } endpoint })
        {
            return null;
        }

        if (!string.Equals(target.ConversationKind, "dm", StringComparison.Ordinal))
        {
            return endpoint;
        }

        if (actor is not { Length: > 0 })
        {
            return null;
        }

        var (iss, sub) = OidcOf(actor);
        var linked = await db.ProviderIdentities.AsNoTracking().AnyAsync(
            i => i.Provider == target.Provider && i.OidcIssuer == iss && i.OidcSubject == sub, ct);
        return linked ? endpoint : null;
    }

    /// <summary>The team's public channel rung: the first enabled chat
    /// provider with a configured public channel for that team (the team
    /// settings row's `publicChannel`), anonymized by the emitter.</summary>
    private async Task<EgressRung?> TeamChannelAsync(
        FacilitatorDbContext db, string teamSlug, CancellationToken ct)
    {
        // Channel-scoped notifications settings may name the team channel
        // explicitly: {"teamChannel": {"provider": ..., "channel": ...}} on
        // the team-scope row, else the system row.
        foreach (var payload in await SettingsAsync(db, teamSlug, ct))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("teamChannel", out var tc)
                    && tc.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                    && tc.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                    && providers.Find(p.GetString()!) is { EndpointId: { Length: > 0 } endpoint })
                {
                    return new EgressRung(
                        new ConversationTarget(p.GetString()!, null, c.GetString()!, null, null, null, "channel", null),
                        endpoint);
                }
            }
            catch (JsonException)
            {
                // next layer
            }
        }

        return null;
    }

    /// <summary>Notifications settings, team layer first, then system.</summary>
    private static async Task<List<string>> SettingsAsync(FacilitatorDbContext db, string teamSlug, CancellationToken ct)
    {
        var payloads = new List<string>();
        var teamRow = await db.TeamSettings.AsNoTracking().FirstOrDefaultAsync(
            s => s.Resource == "notifications" && s.ScopeKind == "team" && s.Team!.Slug == teamSlug, ct);
        if (teamRow is not null)
        {
            payloads.Add(teamRow.Payload);
        }

        var system = await db.AdminConfig.AsNoTracking().FirstOrDefaultAsync(
            c => c.Resource == "notifications", ct);
        if (system is not null)
        {
            payloads.Add(system.Payload);
        }

        return payloads;
    }

    /// <summary>`oidc:&lt;iss&gt;|&lt;sub&gt;` split (the linked-identity key).</summary>
    internal static (string Issuer, string Subject) OidcOf(string canonicalActor)
    {
        var value = canonicalActor.StartsWith("oidc:", StringComparison.Ordinal)
            ? canonicalActor["oidc:".Length..]
            : canonicalActor;
        var bar = value.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? (string.Empty, value) : (value[..bar], value[(bar + 1)..]);
    }
}
