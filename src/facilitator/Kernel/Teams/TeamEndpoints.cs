using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Facilitator.Kernel.Teams;

/// <summary>
/// The team surface: the roster (who belongs, in what capacity) and the
/// per-team switch rows that feed effective config. Authority: an operator
/// touches anything; a team-operator (a roster row of capacity
/// <c>operator</c> — a code-declared built-in role) touches only its own team.
/// </summary>
public static class TeamEndpoints
{
    /// <summary>Map the team-API group.</summary>
    public static IEndpointRouteBuilder MapTeamsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/teams", async (TeamDirectory directory, HttpContext http, CancellationToken ct) =>
            Results.Ok(await directory.ListAsync(ActorOf(http), http.GetFacilitatorPrincipal(), ct)));

        group.MapGet("/teams/{slug}/members", async (string slug, TeamRosterStore roster, HttpContext http, CancellationToken ct) =>
            await ReadAuthorityAsync(http, slug, roster, ct) is { } denial
                ? denial
                : Results.Ok(await roster.ListAsync(slug, ct)));

        group.MapPut("/teams/{slug}/members/{**actor}", async (
            string slug,
            string actor,
            HttpRequest request,
            TeamRosterStore roster,
            TeamDirectory directory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await AuthorityMayAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out var reason))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            actor = Uri.UnescapeDataString(actor);
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                var root = document.RootElement;
                var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var capacity = root.TryGetProperty("capacity", out var c) ? c.GetString() : null;
                if (kind is not ("human" or "contributor") || capacity is not ("member" or "operator"))
                {
                    return Results.Problem(ProblemErrors.Create(
                        "invalid-roster-entry", "Bad request", StatusCodes.Status400BadRequest,
                        "kind must be human|contributor and capacity must be member|operator"));
                }

                var outcome = await roster.PutAsync(slug, actor, kind, capacity, ActorOf(http), ct);
                return outcome.Outcome switch
                {
                    TeamRosterOutcome.TeamUnknown => Results.Problem(ProblemErrors.TeamUnknown(slug)),
                    TeamRosterOutcome.InvalidActor => Results.Problem(ProblemErrors.Create(
                        "invalid-actor", "Bad request", StatusCodes.Status400BadRequest, outcome.Detail ?? "malformed actor")),
                    _ => Results.Ok(await directory.MemberViewAsync(slug, actor, ct)),
                };
            });
        });

        group.MapDelete("/teams/{slug}/members/{**actor}", async (
            string slug,
            string actor,
            HttpRequest request,
            TeamRosterStore roster,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await AuthorityMayAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out _))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            await roster.RemoveAsync(slug, Uri.UnescapeDataString(actor), ActorOf(http), ct);
            return Results.NoContent();
        });

        // Per-team / per-channel switch rows (the team + channel layers of effective config).
        group.MapGet("/teams/{slug}/settings", async (string slug, TeamRosterStore roster, TeamSettingsStore settings, HttpContext http, CancellationToken ct) =>
            await ReadAuthorityAsync(http, slug, roster, ct) is { } denial
                ? denial
                : Results.Ok(await settings.ListAsync(slug, ct)));

        group.MapPut("/teams/{slug}/settings/{resource}", async (
            string slug,
            string resource,
            HttpRequest request,
            TeamRosterStore roster,
            TeamSettingsStore settings,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await AuthorityMayAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out var reason))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            if (!TryParseIfMatch(request.Headers.TryGetValue("If-Match", out var ifMatch) ? ifMatch.ToString() : null, out var expectedVersion))
            {
                return Results.Problem(ProblemErrors.ConfigPreconditionMissing(resource));
            }

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                var root = document.RootElement;
                var scopeKind = root.TryGetProperty("scopeKind", out var sk) ? sk.GetString() : "team";
                var scopeId = root.TryGetProperty("scopeId", out var sid) ? sid.GetString() : slug;
                if (scopeKind is not ("team" or "channel") || string.IsNullOrWhiteSpace(scopeId))
                {
                    return Results.Problem(ProblemErrors.Create(
                        "invalid-scope", "Bad request", StatusCodes.Status400BadRequest,
                        "scopeKind must be team|channel and scopeId must be non-empty"));
                }

                var outcome = await settings.PutAsync(
                    slug,
                    scopeKind,
                    scopeId,
                    resource,
                    root.TryGetProperty("payload", out var payload) ? payload : default,
                    reason!,
                    ActorOf(http),
                    ct,
                    expectedVersion);
                return outcome.Outcome switch
                {
                    TeamSettingOutcome.TeamUnknown => Results.Problem(ProblemErrors.TeamUnknown(slug)),
                    TeamSettingOutcome.Invalid => Results.Problem(ProblemErrors.ConfigInvalid(outcome.Detail ?? "malformed payload")),
                    TeamSettingOutcome.Conflict => Results.Problem(ProblemErrors.ConfigVersionConflict(resource, outcome.StoredVersion)),
                    _ => Results.Ok(outcome.View),
                };
            });
        });

        group.MapGet("/teams/{slug}/effective/{resource}", async (
            string slug,
            string resource,
            string? channel,
            TeamRosterStore roster,
            EffectiveConfigResolver resolver,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await ReadAuthorityAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            var answer = await resolver.ResolveAsync(slug, channel, resource, ct);

            // Every answer names the level it came from — the switch is
            // explainable, never a silent merge.
            var audit = http.RequestServices.GetRequiredService<Facilitator.Kernel.Shared.Identity.AuditWriter>();
            await audit.WriteAsync(new AuditRecord(
                ActorOf(http),
                "teams.effective.resolve",
                $"teams/{slug}/{resource}",
                "allow",
                null,
                null,
                null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["level"] = answer?.Level ?? "absent",
                    ["scope"] = answer?.ScopeId ?? string.Empty,
                })), ct);

            return answer is null
                ? Results.Problem(ProblemErrors.Create(
                    "switch-unset", "Switch unset", StatusCodes.Status404NotFound,
                    $"resource '{resource}' is set at no level for team '{slug}'"))
                : Results.Ok(new { level = answer.Level, scopeId = answer.ScopeId, payload = JsonDocument.Parse(answer.Payload).RootElement.Clone() });
        });

        return app;
    }

    private const string ChangeReasonHint = "the X-Change-Reason header is required (non-empty, <= 512 chars)";

    private static string ActorOf(HttpContext http) =>
        http.GetFacilitatorPrincipal()?.Actor ?? "http-operator";

    private static bool ChangeReason(HttpRequest request, out string? reason)
    {
        reason = request.Headers.TryGetValue("X-Change-Reason", out var values) ? values.ToString() : null;
        return !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512;
    }

    // The admin plane's ETag law: a missing/unparseable header refuses the
    // write; "*" asserts the resource is unseen; an integer is the revision seen.
    private static bool TryParseIfMatch(string? ifMatch, out long? expectedVersion)
    {
        expectedVersion = null;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }

        var header = ifMatch.Trim();
        if (header == "*")
        {
            return true;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        if (!long.TryParse(header.Trim('"'), out var parsed) || parsed <= 0)
        {
            return false;
        }

        expectedVersion = parsed;
        return true;
    }

    /// <summary>
    /// Roster authority: an operator passes anywhere; otherwise the caller must
    /// hold the built-in team-operator role for this exact team. A caller with
    /// no standing in the team is answered 404 — a foreign team's roster is not
    /// even confirmed to exist; a plain member without capacity is answered 403.
    /// </summary>
    private static async Task<IResult?> AuthorityMayAsync(HttpContext http, string slug, TeamRosterStore roster, CancellationToken ct)
    {
        var principal = http.GetFacilitatorPrincipal();
        if (principal is null)
        {
            return Results.Problem(IdentityAuthentication.Unauthorized());
        }

        if (principal.Roles.Contains("kernel-operator") || principal.Roles.Contains("framework-admin"))
        {
            return null;
        }

        if (await roster.IsTeamOperatorAsync(slug, principal.Actor, ct))
        {
            return null;
        }

        return await roster.IsMemberAsync(slug, principal.Actor, ct)
            ? Results.Problem(IdentityAuthentication.Forbidden("teams.roster", ["kernel-operator", "team-operator"]))
            : Results.Problem(ProblemErrors.TeamNotVisible(slug));
    }

    /// <summary>Read authority: an operator anywhere, otherwise a roster member of
    /// the team — or a caller whose group claim names a team whose roster is not
    /// configured yet (an unconfigured team asserts no membership, so it is not
    /// hidden). A foreign team answers 404; its existence is not confirmed.</summary>
    private static async Task<IResult?> ReadAuthorityAsync(HttpContext http, string slug, TeamRosterStore roster, CancellationToken ct)
    {
        var principal = http.GetFacilitatorPrincipal();
        if (principal is null)
        {
            return Results.Problem(IdentityAuthentication.Unauthorized());
        }

        if (principal.Roles.Contains("kernel-operator") || principal.Roles.Contains("framework-admin"))
        {
            return null;
        }

        if (await roster.IsMemberAsync(slug, principal.Actor, ct))
        {
            return null;
        }

        return TeamScope.TeamsOf(principal).Contains(slug, StringComparer.Ordinal)
               && !await roster.IsConfiguredAsync(slug, ct)
            ? null
            : Results.Problem(ProblemErrors.TeamNotVisible(slug));
    }
}
