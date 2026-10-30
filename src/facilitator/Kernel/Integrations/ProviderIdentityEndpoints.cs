using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Integrations;

/// <summary>Redemption request body (human plane: an authenticated OIDC subject
/// presents the DM-delivered code).</summary>
/// <param name="Code">The linking code.</param>
public sealed record RedeemRequest(string Code);

/// <summary>
/// The human-plane linking endpoints (Plan 22, deviation D2): redemption is a
/// normal OIDC-authenticated call — the link workload has no authority to
/// mint human identity mappings. Mounted only when the provider feature is
/// enabled; disabled ⇒ no routes at all.
/// </summary>
public static class ProviderIdentityEndpoints
{
    /// <summary>Map <c>POST /api/link-identities/redeem</c> + list; no-op when disabled.</summary>
    public static IEndpointRouteBuilder MapProviderIdentitiesApi(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<ProviderRegistry>().AnyEnabled)
        {
            return app;
        }

        var group = app.MapGroup("/api/link-identities");

        group.MapPost("/redeem", async (
            RedeemRequest request,
            HttpContext http,
            ProviderEventStore store,
            CancellationToken ct) =>
        {
            var principal = http.GetFacilitatorPrincipal();
            if (principal is null)
            {
                return Results.Problem(ProblemErrors.Create("unauthenticated", "Unauthenticated", StatusCodes.Status401Unauthorized,
                    "redemption requires a verified OIDC identity"));
            }

            var (outcome, detail) = await store.RedeemAsync(request.Code ?? string.Empty, principal.Issuer, principal.Subject, ct);
            return outcome switch
            {
                RedeemOutcome.Redeemed => Results.Ok(detail),
                RedeemOutcome.Unknown => Results.Problem(ProblemErrors.Create("link-challenge-not-found", "Not Found",
                    StatusCodes.Status404NotFound, "no linking challenge carries this code")),
                RedeemOutcome.Consumed => Results.Problem(ProblemErrors.Create("link-challenge-consumed", "Conflict",
                    StatusCodes.Status409Conflict, "the linking code was already redeemed (single use)")),
                _ => Results.Problem(ProblemErrors.Create("link-challenge-expired", "Gone",
                    StatusCodes.Status410Gone, "the linking code window elapsed — send /link again")),
            };
        }).WithName("redeemLinkIdentity").ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410);

        group.MapGet("/", async (HttpContext http, FacilitatorDbContext db, CancellationToken ct) =>
        {
            var subject = http.GetFacilitatorPrincipal()?.Subject ?? string.Empty;
            var rows = await db.ProviderIdentities.AsNoTracking()
                .Where(i => i.OidcSubject == subject)
                .Select(i => new { i.Provider, i.Tenant, i.AccountId, linkedAt = i.UpdatedAt })
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithName("listLinkIdentities");

        return app;
    }
}
