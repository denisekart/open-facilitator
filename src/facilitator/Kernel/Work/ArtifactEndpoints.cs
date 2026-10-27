using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Storage;


namespace Facilitator.Kernel.Work;

/// <summary>
/// The human-plane artifact surface: <c>POST /api/artifacts/request-upload</c>
/// (allocate a slot; the twin of the Link command),
/// <c>PUT /api/artifacts/{id}/content</c> (push bytes — the kernel counts and
/// hashes them, a mismatch rejects the row 422), <c>GET /api/artifacts/{id}</c>
/// (metadata, never the storage URI) and <c>GET /api/artifacts/{id}/content</c>
/// (bytes, served straight from the store with the row's media type + digest
/// ETag). A foreign-team or missing row answers the same 404 everywhere
/// (non-disclosure, the approvals precedent). Human auth is the role-gate
/// middleware (<c>artifacts.read</c>/<c>artifacts.write</c> bindings); team
/// scope is enforced in the ledger.
/// </summary>
public static class ArtifactEndpoints
{
    /// <summary>Map the artifact group.</summary>
    public static IEndpointRouteBuilder MapArtifactsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/artifacts/request-upload",
            async (HttpRequest request, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var body = await RequestBodies.ReadAsync(request, ct);
                if (body.TooLarge)
                {
                    return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
                }

                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body.Text) ? "{}" : body.Text);
                if (!ArtifactArmPolicy.TryParse(doc.RootElement, out var fields, out var problem))
                {
                    return Results.Problem(problem!);
                }

                var principal = http.GetFacilitatorPrincipal();
                if (principal is null)
                {
                    return Results.Problem(IdentityAuthentication.Unauthorized());
                }

                var ticket = await ledger.AllocateAsync(
                    fields.MediaType, fields.Size, fields.Digest, fields.WorkId, fields.AssignmentId,
                    fields.RetentionPolicy, fields.Comment, fields.CommandId,
                    null, principal, "rest", ct);
                return Results.Created($"/api/artifacts/{ticket.ArtifactId}", Ticket(ticket));
            });

        group.MapPut("/artifacts/{id:guid}/content",
            async (Guid id, HttpRequest request, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var principal = http.GetFacilitatorPrincipal();
                if (principal is null)
                {
                    return Results.Problem(IdentityAuthentication.Unauthorized());
                }

                // Team scope first: a foreign team's row answers absent (ledger re-checks
                // state + DB-arbitrated transition underneath).
                var scoped = await ledger.GetAsync(principal.Actor, principal.Groups, principal.Roles, id, ct);
                if (scoped is null)
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return StoreResultAsync(await ledger.StoreAsync(id, request.Body, string.Empty, null, ct), id);
            });

        group.MapGet("/artifacts/{id:guid}",
            async (Guid id, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var principal = http.GetFacilitatorPrincipal()!;
                var view = await ledger.GetAsync(principal.Actor, principal.Groups, principal.Roles, id, ct);
                return view is null
                    ? Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()))
                    : Results.Json(View(view));
            });

        group.MapGet("/artifacts/{id:guid}/content",
            async (Guid id, IArtifactLedger ledger, IArtifactStore store, HttpContext http, CancellationToken ct) =>
            {
                var principal = http.GetFacilitatorPrincipal()!;
                var view = await ledger.GetReadableAsync(principal.Actor, principal.Groups, principal.Roles, id, ct);
                if (view is null)
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                // Object-store backends hand out a presigned GET and answer 302:
                // retrieval bytes bypass the kernel (spec §15.4 portable
                // retrieval). The URL rides the row's own key + the allocation
                // TTL law; HttpClient auto-follows, so consumers see the same
                // bytes with one extra hop.
                if (store is IStoreSignedUrlSource source
                    && await source.PresignDownloadAsync(
                        ArtifactKeys.StoreKeyOf(view.StorageUri), view.MediaType, ArtifactTicket.AllocationTtl, ct) is { } url)
                {
                    return Results.Redirect(url.ToString(), permanent: false);
                }

                var stream = await store.GetAsync(ArtifactKeys.StoreKeyOf(view.StorageUri), ct);
                if (stream is null)
                {
                    // Row says stored but the blob is gone: absent, like any other 404.
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return Results.Stream(stream, view.MediaType,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{view.Digest}\""));
            });

        group.MapPost("/artifacts/{id:guid}/confirm",
            async (Guid id, IArtifactLedger ledger, HttpContext http, CancellationToken ct) =>
            {
                var principal = http.GetFacilitatorPrincipal();
                if (principal is null)
                {
                    return Results.Problem(IdentityAuthentication.Unauthorized());
                }

                // Team scope first (the proxied-PUT twin): a foreign team's row
                // answers absent. The ledger re-checks state + the DB-arbitrated
                // transition + the row's scheme underneath.
                var scoped = await ledger.GetAsync(principal.Actor, principal.Groups, principal.Roles, id, ct);
                if (scoped is null)
                {
                    return Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString()));
                }

                return ConfirmResultAsync(await ledger.ConfirmAsync(id, string.Empty, null, ct), id);
            });

        return app;
    }

    /// <summary>Shared PUT outcome mapping for both planes (the link-plane route reuses it).</summary>
    public static IResult StoreResultAsync(ArtifactStoreResult result, Guid id) => result.Outcome switch
    {
        ArtifactStoreOutcome.Stored or ArtifactStoreOutcome.Idempotent => Results.Json(Stored(id, result.Row)),
        ArtifactStoreOutcome.DigestMismatch => Results.Problem(ProblemErrors.ArtifactDigestMismatch(id.ToString())),
        ArtifactStoreOutcome.SizeMismatch => Results.Problem(ProblemErrors.ArtifactSizeMismatch(id.ToString())),
        _ => Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString())),
    };

    /// <summary>Shared confirm outcome mapping for both planes; a spent slot answers by why (409), never silently.</summary>
    public static IResult ConfirmResultAsync(ArtifactStoreResult result, Guid id) => result.Outcome switch
    {
        ArtifactStoreOutcome.Stored or ArtifactStoreOutcome.Idempotent => Results.Json(Stored(id, result.Row)),
        ArtifactStoreOutcome.DigestMismatch => Results.Problem(ProblemErrors.ArtifactDigestMismatch(id.ToString())),
        ArtifactStoreOutcome.SizeMismatch => Results.Problem(ProblemErrors.ArtifactSizeMismatch(id.ToString())),
        ArtifactStoreOutcome.NotConfirmable => Results.Problem(ProblemErrors.ArtifactNotConfirmable(id.ToString())),
        ArtifactStoreOutcome.NotPending => Results.Problem(ProblemErrors.ArtifactNotPending(id.ToString())),
        _ => Results.Problem(ProblemErrors.ArtifactNotFound(id.ToString())),
    };

    /// <summary>Wire ticket shape (both planes, both ack paths): <c>uploadUrl</c> rides only when the configured adapter mints presigned URLs (absent, not null, when it does not); callers prefer it over <c>uploadLocation</c> when present.</summary>
    public static object Ticket(ArtifactTicket ticket)
    {
        var body = new Dictionary<string, object?>
        {
            ["artifactId"] = ticket.ArtifactId,
            ["uploadLocation"] = ticket.UploadLocation,
            ["method"] = ticket.Method,
            ["expiresAt"] = ticket.ExpiresAt,
        };
        if (ticket.UploadUrl is not null)
        {
            body["uploadUrl"] = ticket.UploadUrl;
        }

        return body;
    }

    internal static object View(ArtifactView view) => new
    {
        artifactId = view.ArtifactId,
        workId = view.WorkId,
        assignmentId = view.AssignmentId,
        mediaType = view.MediaType,
        size = view.Size,
        digest = view.Digest,
        state = view.State,
        verification = view.Verification,
        origin = view.Origin,
        createdBy = view.CreatedBy,
        createdAt = view.CreatedAt,
        storedAt = view.StoredAt,
        retentionPolicy = view.RetentionPolicy,
    };

    private static object Stored(Guid id, ArtifactView? row) => new
    {
        artifactId = id,
        digest = row?.Digest,
        size = row?.Size,
        state = row?.State ?? "stored",
    };
}
