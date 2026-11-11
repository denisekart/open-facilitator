using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Teams;

/// <summary>
/// The contributor surface (K-family): a team registers the non-human actors it
/// owns. Authority is the roster law — an operator anywhere, a team-operator for
/// its own team; a foreign team answers 404. Remit changes are operator actions
/// with a mandatory reason (K7).
/// </summary>
public static partial class TeamEndpoints
{
    private static void MapContributors(RouteGroupBuilder group)
    {
        group.MapGet("/teams/{slug}/contributors", async (
            string slug,
            ContributorStore contributors,
            TeamRosterStore roster,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await ReadAuthorityAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            return Results.Ok(await contributors.ListAsync(slug, ct));
        });

        group.MapPut("/teams/{slug}/contributors/{handle}", async (
            string slug,
            string handle,
            HttpRequest request,
            ContributorStore contributors,
            TeamRosterStore roster,
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

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                var root = document.RootElement;
                var displayName = root.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                var remit = root.TryGetProperty("remit", out var r) ? r : default;
                var outcome = await contributors.PutAsync(
                    slug, handle, displayName, remit, ActorOf(http), reason!, ct);
                return outcome.Outcome switch
                {
                    ContributorOutcome.TeamUnknown => Results.Problem(ProblemErrors.TeamUnknown(slug)),
                    ContributorOutcome.InvalidHandle => Results.Problem(ProblemErrors.Create(
                        "invalid-handle", "Bad request", StatusCodes.Status400BadRequest,
                        outcome.Detail ?? "malformed handle")),
                    ContributorOutcome.InvalidRemit => Results.Problem(ProblemErrors.ConfigInvalid(
                        outcome.Detail ?? "malformed remit")),
                    ContributorOutcome.HandleTaken => Results.Problem(ProblemErrors.Create(
                        "contributor-handle-taken", "Conflict", StatusCodes.Status409Conflict,
                        outcome.Detail ?? "handle belongs to another team")),
                    _ => Results.Ok(await contributors.FindAsync(handle, ct)),
                };
            });
        });

        // Remit-only change (K7): addressed by handle; the authority is the
        // contributor's own team.
        group.MapPut("/contributors/{handle}/remit", async (
            string handle,
            HttpRequest request,
            ContributorStore contributors,
            TeamRosterStore roster,
            HttpContext http,
            CancellationToken ct) =>
        {
            var existing = await contributors.FindAsync(handle, ct);
            if (existing is null)
            {
                return Results.Problem(ProblemErrors.Create(
                    "contributor-unknown", "Not found", StatusCodes.Status404NotFound,
                    $"no contributor '{handle}'"));
            }

            if (await AuthorityMayAsync(http, existing.TeamSlug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out var reason))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                var remit = document.RootElement.TryGetProperty("remit", out var r) ? r : default;
                var outcome = await contributors.PutRemitAsync(handle, remit, ActorOf(http), reason!, ct);
                return outcome.Outcome switch
                {
                    ContributorOutcome.InvalidRemit => Results.Problem(ProblemErrors.ConfigInvalid(
                        outcome.Detail ?? "malformed remit")),
                    ContributorOutcome.ContributorUnknown => Results.Problem(ProblemErrors.Create(
                        "contributor-unknown", "Not found", StatusCodes.Status404NotFound,
                        $"no contributor '{handle}'")),
                    _ => Results.Ok(await contributors.FindAsync(handle, ct)),
                };
            });
        });

        // Retirement (K10): the name endures, the open work moves to the team.
        group.MapPost("/contributors/{handle}/retire", async (
            string handle,
            HttpRequest request,
            ContributorStore contributors,
            TeamRosterStore roster,
            HttpContext http,
            CancellationToken ct) =>
        {
            var existing = await contributors.FindAsync(handle, ct);
            if (existing is null)
            {
                return Results.Problem(ProblemErrors.Create(
                    "contributor-unknown", "Not found", StatusCodes.Status404NotFound,
                    $"no contributor '{handle}'"));
            }

            if (await AuthorityMayAsync(http, existing.TeamSlug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out var reason))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            await contributors.RetireAsync(handle, ActorOf(http), reason!, ct);
            return Results.Ok(await contributors.FindAsync(handle, ct));
        });

        // The contributor's audit trail (who changed its remit, when, and why).
        group.MapGet("/contributors/{handle}/history", async (
            string handle,
            ContributorStore contributors,
            TeamRosterStore roster,
            HttpContext http,
            CancellationToken ct) =>
        {
            var existing = await contributors.FindAsync(handle, ct);
            if (existing is null)
            {
                return Results.Problem(ProblemErrors.Create(
                    "contributor-unknown", "Not found", StatusCodes.Status404NotFound,
                    $"no contributor '{handle}'"));
            }

            if (await ReadAuthorityAsync(http, existing.TeamSlug, roster, ct) is { } denial)
            {
                return denial;
            }

            return Results.Ok(new
            {
                contributor = existing,
                changes = await contributors.HistoryAsync(handle, ct),
            });
        });
    }
}
