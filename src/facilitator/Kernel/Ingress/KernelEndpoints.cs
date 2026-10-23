using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Dispatch;
using Facilitator.Kernel.Persistence;

using Microsoft.AspNetCore.Http.Features;

namespace Facilitator.Kernel.Ingress;

/// <summary>
/// `/api` surface (bootstrap-auth'd, all JSON camelCase). Endpoints stay
/// thin: read the raw body, then delegate to <see cref="IngressProcessor"/>.
/// </summary>
public static class KernelEndpoints
{
    /// <summary>Map the kernel API group.</summary>
    public static IEndpointRouteBuilder MapKernelApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/works", async (HttpRequest request, IngressProcessor processor, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await Parse(body, document => processor.CreateWorkAsync(document.RootElement.Clone(), ct));
        });

        group.MapGet("/works", async (IngressStore store, CancellationToken ct) =>
        {
            var works = await store.ListRecentAsync(ct);
            return Results.Ok(works.Select(IngressProcessor.ToDto).ToArray());
        });

        group.MapGet("/works/{id:guid}", async (Guid id, ICoordinationStore store, CancellationToken ct) =>
        {
            var work = await store.GetAsync(id, ct);
            return work is null
                ? Results.Problem(ProblemErrors.WorkNotFound(id))
                : Results.Ok(IngressProcessor.ToDto(work));
        });

        group.MapPost("/works/{id:guid}/steer", async (Guid id, HttpRequest request, IngressProcessor processor, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await Parse(body, document => processor.SteerAsync(id, document.RootElement.Clone(), ct));
        });

        group.MapPost("/works/{id:guid}/transitions", async (Guid id, HttpRequest request, IngressProcessor processor, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await Parse(body, document => processor.TransitionAsync(id, document.RootElement.Clone(), ct));
        });

        group.MapGet("/works/{id:guid}/timeline", async (Guid id, ICoordinationStore store, CancellationToken ct) =>
        {
            var rows = await store.TimelineAsync(id, ct);
            return Results.Ok(rows.ToArray());
        });

        group.MapPost("/ingress/events", async (HttpRequest request, IngressProcessor processor, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await processor.IngestEventAsync(body.Text, ct);
        });

        group.MapGet("/dev/deliveries", (DeliveryLedger ledger) =>
            Results.Ok(ledger.Snapshot()
                .Select(d => new { deliveryId = d.DeliveryId, attempts = d.Attempts, deliveredAt = d.DeliveredAt })
                .ToArray()));

        return app;
    }

    private sealed record Body(string Text, int Bytes, bool TooLarge);

    private static async Task<Body> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        const int limit = IngressProcessor.MaxBodyBytes;
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }
        if (request.ContentLength > limit)
        {
            return new Body(string.Empty, (int)request.ContentLength!.Value, true);
        }

        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            var bytes = Encoding.UTF8.GetByteCount(text);
            return new Body(text, bytes, bytes > limit);
        }
        catch (BadHttpRequestException)
        {
            // Chunked body exceeded the enforced request limit mid-read.
            return new Body(string.Empty, limit + 1, true);
        }
    }

    /// <summary>Parse a request body to JSON, 400 schema-violation when malformed.</summary>
    private static async Task<IResult> Parse(Body body, Func<JsonDocument, Task<IResult>> handler)
    {
        try
        {
            using var document = JsonDocument.Parse(body.Text.Length == 0 ? "{}" : body.Text);
            return await handler(document);
        }
        catch (JsonException)
        {
            return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
        }
    }
}
