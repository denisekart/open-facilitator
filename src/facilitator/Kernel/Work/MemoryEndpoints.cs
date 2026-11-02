using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Memory;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The memory REST surface: retain / recall / get / promote / correct
/// / share / revoke / delete / export. Bodies are parsed manually (the
/// ApprovalEndpoints law — the kernel validates, never model-binds untrusted
/// JSON); the access context is built from the gate-resolved principal only.
/// Foreign, tombstoned, and unknown entries answer the same
/// <c>memory-not-found</c> 404 — denial discloses nothing.
/// </summary>
public static class MemoryEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Map the memory group beside the manifests surface.</summary>
    public static IEndpointRouteBuilder MapMemoryApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapPost("/memory/retain", async (HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var (data, bad) = await TryBody(http.Request);
            if (bad is not null)
            {
                return bad;
            }

            var request = ParseRetain(data);
            if (request is null)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "retain requires scopeSelector, type (one of the seven §14.4 classifications), content, and provenance{source,evidenceRef,createdBy,createdAt}"));
            }

            var result = await broker.RetainAsync(MemoryAccessContext.From(principal), request, ct);
            return result.Outcome switch
            {
                MemoryOpOutcome.Ok => Results.Created(
                    $"/api/memory/{result.Receipt!.EntryId}",
                    new { entryId = result.Receipt.EntryId, status = result.Receipt.Status, duplicate = result.Receipt.Duplicate }),
                MemoryOpOutcome.QuotaExceeded => Results.Problem(ProblemErrors.MemoryQuotaExceeded(
                    ArtifactLedgerTeam(principal))),
                _ => Results.Problem(ProblemErrors.MemoryScopeInvalid(Req(data, "scopeSelector") ?? "?")),
            };
        });

        group.MapPost("/memory/recall", async (HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var (data, bad) = await TryBody(http.Request);
            if (bad is not null)
            {
                return bad;
            }

            var request = ParseRecall(data);
            if (request is null)
            {
                return Results.Problem(ProblemErrors.SchemaViolation(
                    "recall requires query and purpose (assignment.context | requirements.clarification | report.synthesis)"));
            }

            var response = await broker.RecallAsync(MemoryAccessContext.From(principal), request, ct);
            return Results.Ok(new
            {
                requestId = response.RequestId,
                providerProfile = response.ProviderProfile,
                entries = response.Entries.Select(e => new
                {
                    entryId = e.EntryId,
                    scopeId = e.ScopeId,
                    type = e.Type,
                    content = e.Content,
                    contentHash = e.ContentHash,
                    provenance = e.Provenance,
                    observedAt = e.ObservedAt,
                    effectiveFrom = e.EffectiveFrom,
                    revision = e.Revision,
                    status = e.Status,
                    policyLabel = e.PolicyLabel,
                    accessControl = new { visibility = e.PolicyLabel, allowedPrincipals = new[] { $"team:{e.TeamSlug}" } },
                    retrievalReason = e.RetrievalReason,
                }),
                truncated = response.Truncated,
                asOf = response.AsOf,
            });
        });

        group.MapGet("/memory/export", async (string? scope, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            if (string.IsNullOrWhiteSpace(scope))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("export requires ?scope=<selector>"));
            }

            var ndjson = await broker.ExportAsync(MemoryAccessContext.From(principal), scope, ct);
            return ndjson is null
                ? Results.Problem(ProblemErrors.MemoryScopeInvalid(scope))
                : Results.Text(ndjson, "application/x-ndjson");
        });

        group.MapGet("/memory/{id:guid}", async (Guid id, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var view = await broker.GetAsync(MemoryAccessContext.From(principal), id, ct);
            return view is null
                ? Results.Problem(ProblemErrors.MemoryNotFound(id.ToString()))
                : Results.Ok(new
                {
                    entryId = view.EntryId,
                    scopeId = view.ScopeId,
                    type = view.Type,
                    content = view.Content,
                    mediaType = view.MediaType,
                    contentSummary = view.ContentSummary,
                    contentHash = view.ContentHash,
                    provenance = view.Provenance,
                    observedAt = view.ObservedAt,
                    effectiveFrom = view.EffectiveFrom,
                    classification = view.Classification,
                    policyLabel = view.PolicyLabel,
                    status = view.Status,
                    revision = view.Revision,
                    supersedesEntryId = view.SupersedesEntryId,
                    expiresAt = view.ExpiresAt,
                    createdAt = view.CreatedAt,
                    createdBy = view.CreatedBy,
                    shares = view.Shares.Select(s => new { shareId = s.ShareId, granteeTeam = s.Grantee, createdAt = s.CreatedAt }),
                    usageCount = view.UsageCount,
                });
        });

        group.MapPost("/memory/{id:guid}/promote", async (Guid id, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            string? target = null;
            if (http.Request.ContentLength > 0)
            {
                var (data, bad) = await TryBody(http.Request);
                if (bad is not null)
                {
                    return bad;
                }

                target = Req(data, "targetScope");
            }

            var outcome = await broker.PromoteAsync(MemoryAccessContext.From(principal), id, target, ct);
            return outcome switch
            {
                MemoryOpOutcome.Ok => Results.Ok(new { entryId = id, status = "active" }),
                MemoryOpOutcome.ScopeInvalid => Results.Problem(ProblemErrors.MemoryScopeInvalid(target ?? "?")),
                _ => Results.Problem(ProblemErrors.MemoryNotFound(id.ToString())),
            };
        });

        group.MapPost("/memory/{id:guid}/correct", async (Guid id, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var (data, bad) = await TryBody(http.Request);
            if (bad is not null)
            {
                return bad;
            }

            var content = Req(data, "content");
            if (string.IsNullOrEmpty(content))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("correct requires content"));
            }

            var outcome = await broker.CorrectAsync(
                MemoryAccessContext.From(principal), id, content, Req(data, "contentSummary"), ct);
            return outcome == MemoryOpOutcome.Ok
                ? Results.Ok(new { entryId = id, superseded = true })
                : Results.Problem(ProblemErrors.MemoryNotFound(id.ToString()));
        });

        group.MapPost("/memory/{id:guid}/shares", async (Guid id, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var (data, bad) = await TryBody(http.Request);
            if (bad is not null)
            {
                return bad;
            }

            var grantee = Req(data, "granteeTeam");
            if (string.IsNullOrEmpty(grantee))
            {
                return Results.Problem(ProblemErrors.SchemaViolation("shares requires granteeTeam"));
            }

            var outcome = await broker.ShareAsync(MemoryAccessContext.From(principal), id, grantee, ct);
            return outcome switch
            {
                MemoryOpOutcome.Ok => Results.Ok(new { entryId = id, granteeTeam = grantee }),
                MemoryOpOutcome.ShareDenied => Results.Problem(ProblemErrors.MemoryShareDenied(
                    $"'{grantee}': private entries are unsharable and the grantee must exist in the team roster")),
                _ => Results.Problem(ProblemErrors.MemoryNotFound(id.ToString())),
            };
        });

        group.MapDelete("/memory/{id:guid}/shares/{shareId:guid}", async (Guid id, Guid shareId, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var outcome = await broker.RevokeShareAsync(MemoryAccessContext.From(principal), id, shareId, ct);
            return outcome switch
            {
                MemoryOpOutcome.Ok => Results.NoContent(),
                MemoryOpOutcome.ShareNotFound => Results.Problem(ProblemErrors.MemoryNotFound(id.ToString())),
                _ => Results.Problem(ProblemErrors.MemoryNotFound(id.ToString())),
            };
        });

        group.MapDelete("/memory/{id:guid}", async (Guid id, HttpContext http, MemoryBroker broker, CancellationToken ct) =>
        {
            var principal = Principal(http, out var unauthorized);
            if (principal is null)
            {
                return unauthorized;
            }

            var outcome = await broker.DeleteAsync(MemoryAccessContext.From(principal), id, ct);
            return outcome == MemoryOpOutcome.Ok
                ? Results.NoContent()
                : Results.Problem(ProblemErrors.MemoryNotFound(id.ToString()));
        });

        return app;
    }

    private static FacilitatorPrincipal? Principal(HttpContext http, out IResult unauthorized)
    {
        var principal = http.GetFacilitatorPrincipal();
        unauthorized = principal is null
            ? Results.Problem(IdentityAuthentication.Unauthorized())
            : null!;
        return principal;
    }

    private static async Task<(JsonElement Data, IResult? Bad)> TryBody(HttpRequest request)
    {
        var body = await RequestBodies.ReadAsync(request, CancellationToken.None);
        if (body.TooLarge)
        {
            return (default, Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes)));
        }

        try
        {
            using var doc = JsonDocument.Parse(body.Text.Length == 0 ? "{}" : body.Text);
            var data = doc.RootElement.Clone();
            return data.ValueKind == JsonValueKind.Object
                ? (data, null)
                : (default, Results.Problem(ProblemErrors.SchemaViolation("request body is not a JSON object")));
        }
        catch (JsonException)
        {
            return (default, Results.Problem(ProblemErrors.SchemaViolation("request body is not a JSON object")));
        }
    }

    private static string? Req(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static MemoryRetainRequest? ParseRetain(JsonElement data)
    {
        var selector = Req(data, "scopeSelector");
        var type = Req(data, "type");
        var content = Req(data, "content");
        if (selector is null || content is null
            || type is null || !MemoryBroker.WireTypes.Contains(type)
            || !data.TryGetProperty("provenance", out var prov) || prov.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = Req(prov, "source");
        var evidenceRef = Req(prov, "evidenceRef");
        var createdBy = Req(prov, "createdBy");
        var createdRaw = Req(prov, "createdAt");
        if (source is null || evidenceRef is null || createdBy is null
            || createdRaw is null || !DateTime.TryParse(createdRaw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt))
        {
            return null;
        }

        var label = Req(data, "policyLabel");
        if (label is not null && !MemoryBroker.WirePolicyLabels.Contains(label))
        {
            return null;
        }

        var classification = Req(data, "classification");
        if (classification is not null && classification is not ("internal" or "public"))
        {
            return null;
        }

        DateTime? observedAt = null;
        if (Req(data, "observedAt") is { } observedRaw)
        {
            if (!DateTime.TryParse(observedRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedObserved))
            {
                return null;
            }

            observedAt = parsedObserved;
        }

        return new MemoryRetainRequest(
            selector, type, content,
            new MemoryProvenanceInput(
                source, evidenceRef, createdBy, createdAt.ToUniversalTime(),
                Req(prov, "workId"), Req(prov, "executionId"), Req(prov, "sourceDigest")),
            MediaType: Req(data, "mediaType"),
            ContentSummary: Req(data, "contentSummary"),
            PolicyLabel: label,
            Classification: classification,
            ObservedAt: observedAt);
    }

    private static MemoryRecallRequest? ParseRecall(JsonElement data)
    {
        var query = Req(data, "query");
        var purpose = Req(data, "purpose");
        if (query is null || purpose is null
            || purpose is not ("assignment.context" or "requirements.clarification" or "report.synthesis"))
        {
            return null;
        }

        List<string>? selectors = null;
        if (data.TryGetProperty("scopeSelectors", out var hints) && hints.ValueKind == JsonValueKind.Array)
        {
            selectors = [];
            foreach (var hint in hints.EnumerateArray())
            {
                if (hint.ValueKind == JsonValueKind.String && hint.GetString() is { } s)
                {
                    selectors.Add(s);
                }
            }
        }

        int? maxItems = data.TryGetProperty("maxItems", out var mi) && mi.TryGetInt32(out var parsedItems) ? parsedItems : null;
        int? maxTokens = data.TryGetProperty("maxTokens", out var mt) && mt.TryGetInt32(out var parsedTokens) ? parsedTokens : null;
        var includeSuperseded = data.TryGetProperty("includeSuperseded", out var iss) && iss.ValueKind == JsonValueKind.True;
        return new MemoryRecallRequest(Req(data, "query")!, purpose, Req(data, "workId"), selectors, maxItems, maxTokens, includeSuperseded);
    }

    private static string ArtifactLedgerTeam(FacilitatorPrincipal principal) =>
        Facilitator.Kernel.Shared.Storage.ArtifactLedger.TeamOfGroups(principal.Groups) ?? "unknown";

    /// <summary>Json options kept for the serializer shape (web casing).</summary>
    internal static JsonSerializerOptions WireJson => Json;
}
