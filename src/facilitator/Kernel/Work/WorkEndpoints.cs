using System.Diagnostics;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Teams;

namespace Facilitator.Kernel.Work;

/// <summary>
/// `/api` surface (bootstrap-auth'd, all JSON camelCase). Endpoints stay
/// thin: read the raw body, then delegate to <see cref="IngressProcessor"/>
/// / <see cref="CloudEventIngress"/>.
/// </summary>
public static class WorkEndpoints
{
    /// <summary>Map the kernel work-API group.</summary>
    public static IEndpointRouteBuilder MapWorkApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/works", async (HttpRequest request, IngressProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            // W3C response carrier: the client learns the request trace id and
            // can correlate the dispatch trace the outbox stamps downstream.
            http.Response.Headers["traceparent"] = Activity.Current?.Id;

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.CreateWorkAsync(document.RootElement.Clone(), ActorOf(http), ct));
        });

        group.MapGet("/works", async (IngressStore store, TeamScope scope, HttpContext http, CancellationToken ct) =>
        {
            var works = await store.ListRecentAsync(VisibleTeamsOf(http, scope), ct);
            return Results.Ok(works.Select(WorkDtoMapper.ToDto).ToArray());
        });

        group.MapGet("/works/{id:guid}", async (Guid id, ICoordinationStore store, TeamScope scope, HttpContext http, CancellationToken ct) =>
        {
            var work = await store.GetAsync(id, ct);
            if (work is null)
            {
                return Results.Problem(ProblemErrors.WorkNotFound(id));
            }

            // A merged work still resolves: its history stands, and the read
            // redirects to the survivor (303, never a rewritten id).
            if (work.MergedInto is { } into)
            {
                return new RedirectSeeOther($"/api/works/{into}");
            }

            var team = (await scope.TeamOfWorkAsync(id, ct)).FirstOrDefault();
            return await scope.MayReadAsync(http.GetFacilitatorPrincipal(), team, ct)
                ? Results.Ok(WorkDtoMapper.ToDto(work))
                : Results.Problem(ProblemErrors.WorkNotFound(id));
        });

        group.MapPost("/works/{id:guid}/merge", async (Guid id, HttpRequest request, IngressProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.MergeAsync(id, document.RootElement.Clone(), ActorOf(http), ct));
        });

        group.MapPost("/works/{id:guid}/cost", async (Guid id, HttpRequest request, ICoordinationStore store, TeamScope scope, HttpContext http, CancellationToken ct) =>
        {
            var team = (await scope.TeamOfWorkAsync(id, ct)).FirstOrDefault();
            if (!await scope.MayReadAsync(http.GetFacilitatorPrincipal(), team, ct))
            {
                return Results.Problem(ProblemErrors.WorkNotFound(id));
            }

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            // The executor-reported cost: {tokens,units,currency,source,at}
            // stored verbatim - the kernel never interprets money.
            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                await store.SetCostAsync(id, document.RootElement.GetRawText(), ct);
                return Results.NoContent();
            });
        });

        group.MapGet("/works/{id:guid}/links", async (Guid id, ICoordinationStore store, TeamScope scope, HttpContext http, CancellationToken ct) =>
        {
            var team = (await scope.TeamOfWorkAsync(id, ct)).FirstOrDefault();
            if (!await scope.MayReadAsync(http.GetFacilitatorPrincipal(), team, ct))
            {
                return Results.Problem(ProblemErrors.WorkNotFound(id));
            }

            return Results.Ok((await store.LinksAsync(id, ct)).ToArray());
        });

        group.MapPost("/works/{id:guid}/steer", async (Guid id, HttpRequest request, IngressProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.SteerAsync(id, document.RootElement.Clone(), ct));
        });

        group.MapPost("/works/{id:guid}/transitions", async (Guid id, HttpRequest request, IngressProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.TransitionAsync(id, document.RootElement.Clone(), ActorOf(http), ct));
        });

        group.MapPost("/works/{id:guid}/redispatch", async (Guid id, HttpRequest request, IngressProcessor processor, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.RedispatchAsync(id, document.RootElement.Clone(), ct));
        });

        group.MapGet("/works/{id:guid}/timeline", async (Guid id, ICoordinationStore store, TeamScope scope, HttpContext http, CancellationToken ct) =>
        {
            var team = (await scope.TeamOfWorkAsync(id, ct)).FirstOrDefault();
            if (!await scope.MayReadAsync(http.GetFacilitatorPrincipal(), team, ct))
            {
                return Results.Problem(ProblemErrors.WorkNotFound(id));
            }

            var rows = await store.TimelineAsync(id, ct);
            return Results.Ok(rows.ToArray());
        });

        group.MapPost("/ingress/events", async (HttpRequest request, CloudEventIngress ingress, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await ingress.IngestEventAsync(body.Text, ct);
        });


        return app;
    }

    /// <summary>303 See Other: the answer lives at the survivor's URL.</summary>
    private sealed class RedirectSeeOther(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }

    private static string ActorOf(HttpContext http) =>
        http.GetFacilitatorPrincipal()?.Actor ?? "http-operator";

    /// <summary>
    /// Team slugs a read is filtered to: null (unfiltered) for a privileged or
    /// team-less caller, otherwise exactly the caller's teams.
    /// </summary>
    private static IReadOnlyList<string>? VisibleTeamsOf(HttpContext http, TeamScope scope)
    {
        var principal = http.GetFacilitatorPrincipal();
        if (principal is null
            || principal.Roles.Contains("kernel-operator")
            || principal.Roles.Contains("framework-admin"))
        {
            return null;
        }

        var teams = TeamScope.TeamsOf(principal);
        return teams.Count == 0 ? null : teams;
    }
}
