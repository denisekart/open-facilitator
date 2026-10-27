using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Storage;

namespace Facilitator.Kernel.Work;

/// <summary>The work-scoped result-manifest surface (Plan 15): pull-reads over the recorded chain.</summary>
public static class ResultManifestEndpoints
{
    /// <summary>Map the work-scoped manifests group.</summary>
    public static IEndpointRouteBuilder MapResultManifestsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/works/{id:guid}/manifests", async (Guid id, HttpContext http, IResultManifestLedger ledger, CancellationToken ct) =>
        {
            var principal = http.GetFacilitatorPrincipal();
            if (principal is null)
            {
                return Results.Problem(IdentityAuthentication.Unauthorized());
            }

            // Work-scope 404 law (approvals precedent, hardened): an unknown
            // work and a foreign team's work answer the same 404.
            var rows = await ledger.ListForWorkAsync(id, principal.Groups, principal.Roles, ct);
            return rows is null
                ? Results.Problem(ProblemErrors.WorkNotFound(id))
                : Results.Ok(rows.Select(ToDto).ToArray());
        });

        return app;
    }

    private static object ToDto(ResultManifestView row) => new
    {
        manifestId = row.ManifestId,
        workId = row.WorkId,
        assignmentId = row.AssignmentId,
        executionId = row.ExecutionId,
        revision = row.Revision,
        revisionOf = row.RevisionOf,
        digest = row.Digest,
        structuredDigest = row.StructuredDigest,
        artifactId = row.ArtifactId,
        resultState = row.ResultState,
        state = row.State,
        recordedAt = row.RecordedAt,
    };
}
