using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The human input surface: <c>POST /api/works/{id}/interactions/{requestId}/respond</c>.
/// The authed principal IS the human (gate middleware ahead of the handler),
/// so respond acts with the token's canonical actor and mints the
/// <c>kernel:</c> evidence receipt the approval respond law (D5) echoes back.
/// A requestId belonging to another work answers 404 like a missing one — no
/// cross-work existence leak; a second answer is a 409 (command-ledger dedupe,
/// id <c>input:&lt;requestId&gt;</c>); a superseded execution generation is a
/// 409 fence mismatch.
/// </summary>
public static class InteractionEndpoints
{
    /// <summary>Map the work-scoped interactions group.</summary>
    public static IEndpointRouteBuilder MapInteractionsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/works/{id:guid}/interactions/{requestId}/respond",
            async (Guid id, string requestId, HttpRequest request, InteractionIngress interactions, HttpContext http, CancellationToken ct) =>
            {
                var body = await RequestBodies.ReadAsync(request, ct);
                if (body.TooLarge)
                {
                    return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
                }

                return await RequestBodyJson.ParseAsync(body, async document =>
                {
                    var problem = ValidateRespondBody(document.RootElement, out var answer);
                    if (problem is not null)
                    {
                        return Results.Problem(problem);
                    }

                    var principal = http.GetFacilitatorPrincipal();
                    if (principal is null)
                    {
                        return Results.Problem(ProblemErrors.ApprovalActorUnknown());
                    }

                    var arm = await interactions.HandleRespondAsync(
                        id, requestId, answer!, principal.Actor, principal.Groups, ct);
                    if (arm.Refused)
                    {
                        return Results.Problem(arm.Problem!);
                    }

                    return Results.Ok(new { status = arm.Status, requestId, evidenceRef = arm.EvidenceRef });
                });
            });

        return app;
    }

    // {answer: string 1..16384, actorEvidenceRef?: string}; unknown fields
    // are a 400 schema-violation (the command schema's law, mirrored). The
    // REST surface mints its own kernel: receipt, so a supplied
    // actorEvidenceRef is accepted and ignored.
    private static ProblemDetails? ValidateRespondBody(JsonElement root, out string? answer)
    {
        answer = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("answer" or "actorEvidenceRef"))
            {
                return ProblemErrors.SchemaViolation($"unknown field '{property.Name}'");
            }
        }

        if (!root.TryGetProperty("answer", out var answerElement) || answerElement.ValueKind != JsonValueKind.String)
        {
            return ProblemErrors.SchemaViolation("answer must be a string");
        }

        var value = answerElement.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > 16384)
        {
            return ProblemErrors.SchemaViolation("answer must be 1..16384 characters");
        }

        answer = value;
        return null;
    }
}
