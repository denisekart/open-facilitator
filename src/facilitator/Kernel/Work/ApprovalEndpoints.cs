using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The human approval surface (Plan 11): <c>GET /api/works/{id}/approvals</c> +
/// <c>POST /api/works/{id}/approvals/{approvalId}/respond</c>. The authed
/// principal IS the human (Plan 09 gate middleware + role gate ahead of
/// these handlers), so respond acts with the token's canonical actor, groups,
/// and display name — eligibility is enforced inside the ledger (law D4) and
/// a consumed approve mints the <c>kernel:</c> evidence receipt (law D5) that
/// the response echoes as <c>evidenceRef</c>. An approval belonging to another
/// work answers 404 like a missing one — no cross-work existence leak.
/// </summary>
public static class ApprovalEndpoints
{
    /// <summary>Map the work-scoped approvals group.</summary>
    public static IEndpointRouteBuilder MapApprovalsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/works/{id:guid}/approvals", async (Guid id, IApprovalLedger ledger, CancellationToken ct) =>
        {
            var rows = await ledger.ListForWorkAsync(id, ct);
            return Results.Ok(rows.Select(ToDto).ToArray());
        });

        group.MapPost("/works/{id:guid}/approvals/{approvalId:guid}/respond",
            async (Guid id, Guid approvalId, HttpRequest request, IApprovalLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var body = await RequestBodies.ReadAsync(request, ct);
                if (body.TooLarge)
                {
                    return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
                }

                return await RequestBodyJson.ParseAsync(body, async document =>
                {
                    var problem = ValidateRespondBody(document.RootElement, out var decision, out var comment);
                    if (problem is not null)
                    {
                        return Results.Problem(problem);
                    }

                    var principal = http.GetFacilitatorPrincipal();
                    if (principal is null)
                    {
                        return Results.Problem(ProblemErrors.ApprovalActorUnknown());
                    }

                    // Work-scope check first: another work's approval is a 404
                    // (GetAsync also lazy-expires the row, law D3).
                    var row = await ledger.GetAsync(approvalId, ct);
                    if (row is null || row.WorkId != id)
                    {
                        return Results.Problem(ProblemErrors.ApprovalNotFound(approvalId.ToString()));
                    }

                    var result = await ledger.RespondAsync(
                        approvalId.ToString(), decision!, comment,
                        principal.Actor, principal.Groups, principal.DisplayName,
                        evidenceRef: null, mintEvidence: true, ct);
                    return result.Outcome switch
                    {
                        ApprovalDecisionOutcome.Decided => Results.Ok(new
                        {
                            status = "decided",
                            approvalId,
                            state = result.Row?.State,
                            evidenceRef = result.EvidenceRef,
                        }),
                        ApprovalDecisionOutcome.Expired => Results.Problem(ProblemErrors.ApprovalExpired(approvalId.ToString())),
                        ApprovalDecisionOutcome.Revoked => Results.Problem(ProblemErrors.ApprovalRevoked(approvalId.ToString())),
                        ApprovalDecisionOutcome.Ineligible => Results.Problem(ProblemErrors.ApprovalActorIneligible(approvalId.ToString())),
                        _ => Results.Problem(ProblemErrors.ApprovalAlreadyConsumed(approvalId.ToString())),
                    };
                });
            });

        return app;
    }

    private static object ToDto(ApprovalView view) => new
    {
        approvalId = view.ApprovalId,
        workId = view.WorkId,
        actionDigest = view.ActionDigest,
        state = view.State,
        digestVerification = view.DigestVerification,
        eligibleActors = view.EligibleActors,
        expiresAt = view.ExpiresAt,
        createdAt = view.CreatedAt,
        consumedAt = view.ConsumedAt,
        decidedBy = view.DecidedBy,
        comment = view.Comment,
        decidedAt = view.DecidedAt,
    };

    // {decision: "approved"|"denied", comment?: string <= 1024}; unknown
    // fields are a 400 schema-violation (the command schema's law, mirrored).
    private static Microsoft.AspNetCore.Mvc.ProblemDetails? ValidateRespondBody(
        JsonElement root, out string? decision, out string? comment)
    {
        decision = null;
        comment = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ProblemErrors.SchemaViolation("respond body must be a JSON object");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("decision" or "comment"))
            {
                return ProblemErrors.SchemaViolation($"unknown field '{property.Name}'");
            }
        }

        if (!root.TryGetProperty("decision", out var decisionElement)
            || decisionElement.ValueKind != JsonValueKind.String
            || decisionElement.GetString() is not ("approved" or "denied"))
        {
            return ProblemErrors.SchemaViolation("decision must be \"approved\" or \"denied\"");
        }

        decision = decisionElement.GetString();
        if (root.TryGetProperty("comment", out var commentElement))
        {
            if (commentElement.ValueKind != JsonValueKind.String)
            {
                return ProblemErrors.SchemaViolation("comment must be a string");
            }

            if (commentElement.GetString() is { Length: > 1024 })
            {
                return ProblemErrors.SchemaViolation("comment exceeds 1024 characters");
            }

            comment = commentElement.GetString();
        }

        return null;
    }
}
