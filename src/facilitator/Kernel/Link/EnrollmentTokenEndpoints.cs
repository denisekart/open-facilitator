using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Link;

/// <summary>
/// <c>/api/admin/enroll-tokens</c>: mintable, revocable
/// enrollment tokens for mode-(a) enroll. Authz is 100% config-driven
/// (<c>enrollment-tokens.manage</c> in <c>identity:authorization:operations</c>)
/// — no <c>.RequireAuthorization</c> anywhere. The token literal is returned
/// ONLY on mint; a minted token consumed by an endpoint is that endpoint's
/// standing re-enroll secret until revoked (revoke kills the issued link
/// enrollment too).
/// </summary>
public static class EnrollmentTokenEndpoints
{
    private const int MaxFieldLength = 200;
    private const int DefaultTtlMinutes = 60;
    private const int MinTtlMinutes = 1;
    private const int MaxTtlMinutes = 10080;

    private sealed record MintRequest(string? Label, string? Team, string? Role, int? TtlMinutes);

    private sealed record MintResponse(Guid Id, string Token, DateTime ExpiresAt);

    /// <summary>Map the enroll-token admin group (mount next to the other admin routes).</summary>
    public static IEndpointRouteBuilder MapEnrollmentTokenApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/enroll-tokens");

        group.MapPost("/", async (HttpRequest request, EnrollmentTokenStore store, LinkEnrollmentStore enrollments, HttpContext http, CancellationToken ct) =>
        {
            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            MintRequest? dto;
            try
            {
                dto = JsonSerializer.Deserialize<MintRequest>(body.Text, LinkEndpoints.Web);
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("body is not valid JSON"));
            }

            if (dto is null
                || string.IsNullOrWhiteSpace(dto.Label) || dto.Label.Length > MaxFieldLength
                || string.IsNullOrWhiteSpace(dto.Team) || dto.Team.Length > MaxFieldLength)
            {
                return Results.Problem(ProblemErrors.SchemaViolation("label and team are required, at most 200 characters each"));
            }

            if (dto.Role is null || !LinkEnrollmentStore.Roles.Contains(dto.Role, StringComparer.Ordinal))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("role must be gateway or child"));
            }

            if (!await enrollments.TeamExistsAsync(dto.Team, ct))
            {
                return Results.Problem(ProblemErrors.TeamUnknown(dto.Team));
            }

            var ttl = Math.Clamp(dto.TtlMinutes ?? DefaultTtlMinutes, MinTtlMinutes, MaxTtlMinutes);
            var minted = await store.MintAsync(dto.Label, dto.Team, dto.Role, ttl, http.GetFacilitatorPrincipal()?.Actor, ct);
            return Results.Created($"/api/admin/enroll-tokens/{minted.Id}",
                new MintResponse(minted.Id, minted.Token, minted.ExpiresAt));
        });

        group.MapGet("/", async (EnrollmentTokenStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        group.MapDelete("/{id:guid}", async (Guid id, EnrollmentTokenStore store, LinkEnrollmentStore enrollments, HttpContext http, CancellationToken ct) =>
        {
            var (outcome, consumedEndpointId) = await store.RevokeAsync(id, http.GetFacilitatorPrincipal()?.Actor, ct);
            if (outcome == RevokeTokenOutcome.NotFound)
            {
                return Results.Problem(ProblemErrors.EnrollmentTokenNotFound(id));
            }

            // The invalidation lever: the issued link token dies with the token.
            if (outcome == RevokeTokenOutcome.Revoked && consumedEndpointId is not null)
            {
                await enrollments.RevokeAsync(consumedEndpointId, ct);
            }

            return Results.NoContent();
        });

        return app;
    }
}
