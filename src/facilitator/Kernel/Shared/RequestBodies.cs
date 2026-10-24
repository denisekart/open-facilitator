using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Features;

namespace Facilitator.Kernel.Shared;

/// <summary>
/// The ONE request-body reader for every JSON endpoint: UTF-8 text plus the
/// size gate (413 answer built by the caller from <see cref="TooLarge"/>).
/// </summary>
public static class RequestBodies
{
    /// <summary>Wire limit every ingress body is checked against.</summary>
    public const int MaxBodyBytes = 262144;

    /// <summary>Read the body under the enforced limit; TooLarge when past it.</summary>
    public static async Task<Body> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        const int limit = MaxBodyBytes;
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

    /// <summary>Parsed body text, its byte size, and whether the gate rejected it.</summary>
    public sealed record Body(string Text, int Bytes, bool TooLarge);
}

/// <summary>Body-plus-JSON parse for endpoint handlers (400 schema-violation when malformed).</summary>
public static class RequestBodyJson
{
    /// <summary>Parse the (already size-gated) body to JSON; empty bodies parse as <c>{}</c>.</summary>
    public static async Task<IResult> ParseAsync(RequestBodies.Body body, Func<JsonDocument, Task<IResult>> handler)
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
