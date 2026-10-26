using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

namespace Facilitator.Kernel.Work;

/// <summary>
/// `/api` surface (bootstrap-auth'd, all JSON camelCase). Endpoints stay
/// thin: read the raw body, then delegate to <see cref="IngressProcessor"/>
/// / <see cref="CloudEventIngress"/>.
/// </summary>
public static class WorkEndpoints
{
    /// <summary>Map the kernel work-API group.</summary>
    public static IEndpointRouteBuilder MapWorkApi(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/works", async (HttpRequest request, IngressProcessor processor, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, document => processor.CreateWorkAsync(document.RootElement.Clone(), ActorOf(http), ct));
        });

        group.MapGet("/works", async (IngressStore store, CancellationToken ct) =>
        {
            var works = await store.ListRecentAsync(ct);
            return Results.Ok(works.Select(WorkDtoMapper.ToDto).ToArray());
        });

        group.MapGet("/works/{id:guid}", async (Guid id, ICoordinationStore store, CancellationToken ct) =>
        {
            var work = await store.GetAsync(id, ct);
            return work is null
                ? Results.Problem(ProblemErrors.WorkNotFound(id))
                : Results.Ok(WorkDtoMapper.ToDto(work));
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

        group.MapGet("/works/{id:guid}/timeline", async (Guid id, ICoordinationStore store, CancellationToken ct) =>
        {
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

        if (environment.IsDevelopment())
        {
            group.MapGet("/dev/deliveries", (DeliveryLedger ledger) =>
                Results.Ok(ledger.Snapshot()
                    .Select(d => new { deliveryId = d.DeliveryId, attempts = d.Attempts, deliveredAt = d.DeliveredAt })
                    .ToArray()));
        }

        return app;
    }

    private static string ActorOf(HttpContext http) =>
        http.GetFacilitatorPrincipal()?.Actor ?? "http-operator";
}
