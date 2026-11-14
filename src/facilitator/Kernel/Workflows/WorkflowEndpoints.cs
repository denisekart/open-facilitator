using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// <c>/api/admin/workflows/*</c>: the definition plane's operator surface.
/// Endpoints stay thin (parse, delegate, map); authorization is config
/// (<c>admin.read</c>/<c>admin.mutate</c> path bindings) exactly like the
/// admin plane, mutations carry <c>X-Change-Reason</c> + <c>If-Match</c> and
/// leave audit rows.
/// </summary>
public static class WorkflowEndpoints
{
    /// <summary>Map the workflows group inside the admin group.</summary>
    public static IEndpointRouteBuilder MapWorkflowApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/workflows");

        group.MapGet("/definitions", async (WorkflowDefinitionStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        // Editor population: closed sets the UI may only SELECT from.
        group.MapGet("/registry", async (
            WorkflowDefinitionStore store,
            FacilitatorDbContext db,
            Ofac.Protocol.Validation.EnvelopeValidator validator,
            CancellationToken ct) =>
        {
            var eventTypes = validator.SchemaIds
                .Where(static id => id.Contains("/event.") && id.EndsWith(".v1.json", StringComparison.Ordinal))
                .Select(static id => "org.facilitator." + id[(id.IndexOf("/v1/", StringComparison.Ordinal) + 4)..^".json".Length])
                .Order(StringComparer.Ordinal)
                .ToList();
            var commands = Ofac.Protocol.Validation.KnownCommands.All.Order(StringComparer.Ordinal).ToList();
            var manifestTexts = await db.LinkEnrollments.AsNoTracking()
                .Where(static e => e.RevokedAt == null)
                .Select(static e => e.CapabilityManifest)
                .ToListAsync(ct);
            var capabilities = manifestTexts
                .Where(static m => m is not null)
                .SelectMany(m => JsonDocument.Parse(m!).RootElement.GetProperty("capabilities").EnumerateArray())
                .Select(static c => c.GetProperty("id").GetString() ?? "")
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
            var callable = (await store.ListAsync(ct))
                .Where(static d => d.Enabled)
                .Select(static d => new { d.Id, d.Version, d.Title })
                .ToList();
            var teams = await db.Teams.AsNoTracking().Select(static t => t.Slug).Order().ToListAsync(ct);
            return Results.Ok(new { eventTypes, commands, capabilities, callable, teams });
        });

        group.MapGet("/definitions/export", async (WorkflowDefinitionStore store, CancellationToken ct) =>
        {
            var rows = await store.AllVersionsAsync(ct);
            return Results.Ok(new
            {
                directory = "kernel-gallery",
                files = rows.Select(d => new
                {
                    name = $"{d.DefId}@{d.Version}.yaml",
                    yaml = WorkflowDoc.ToYaml(JsonDocument.Parse(d.Payload).RootElement),
                }).ToList(),
            });
        });

        // Export listing only: a file whose id/version collide with a stored
        // row skips (import never overwrites — the seeder's law).
        group.MapPost("/definitions/import", async (
            HttpRequest request,
            HttpContext http,
            WorkflowDefinitionStore store,
            WorkflowSubscriptionRegistry subscriptions,
            CancellationToken ct) =>
        {
            var actor = Mutation.RequireActor(http, request, out var reason, out var problem);
            if (problem is not null)
            {
                return problem;
            }

            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (!body.RootElement.TryGetProperty("files", out var files))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("body must carry a files array"));
            }

            var results = new List<object>();
            foreach (var file in files.EnumerateArray())
            {
                var text = file.GetProperty("content").GetString() ?? string.Empty;
                JsonElement doc;
                try
                {
                    doc = WorkflowDoc.Parse(text);
                }
                catch (FormatException ex)
                {
                    results.Add(new { file = file.GetProperty("name").GetString(), outcome = "invalid", detail = ex.Message });
                    continue;
                }

                if (!doc.TryGetProperty("id", out var docId) || docId.ValueKind != JsonValueKind.String)
                {
                    results.Add(new { file = file.GetProperty("name").GetString(), outcome = "invalid", detail = "document carries no id" });
                    continue;
                }

                var etag = file.TryGetProperty("ifMatch", out var im) ? im.GetString() : null;
                var outcome = await store.PutAsync(
                    docId.GetString()!, doc, etag ?? "*", actor!, reason!, forceEnabled: false, ct);
                results.Add(new
                {
                    file = file.GetProperty("name").GetString(),
                    outcome = outcome.Outcome.ToString().ToLowerInvariant(),
                    detail = outcome.Detail,
                });
            }

            return Results.Ok(new { results });
        });

        group.MapGet("/definitions/{id}", async (
            string id,
            int? version,
            HttpRequest request,
            WorkflowDefinitionStore store,
            CancellationToken ct) =>
        {
            var view = await store.GetAsync(id, version, ct);
            if (view is null)
            {
                return Results.Problem(ProblemErrors.Create(
                    "definition-not-found", "Workflow definition not found", StatusCodes.Status404NotFound, $"no definition '{id}'"));
            }

            var accept = request.Headers.Accept.ToString();
            if (accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Text(view.Yaml, "application/yaml");
            }

            return Results.Ok(new
            {
                view.Summary,
                document = view.Document,
                yaml = view.Yaml,
                view.CreatedAt,
                view.CreatedBy,
            });
        });

        group.MapPut("/definitions/{id}", async (
            string id,
            HttpRequest request,
            HttpContext http,
            WorkflowDefinitionStore store,
            WorkflowSubscriptionRegistry subscriptions,
            CancellationToken ct) =>
        {
            var actor = Mutation.RequireActor(http, request, out var reason, out var problem);
            if (problem is not null)
            {
                return problem;
            }

            var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
            if (!request.HasJsonContentType()
                && !string.Equals(mediaType, "application/yaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "text/yaml", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("body must be application/json or application/yaml"));
            }

            var rawBody = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            if (!WorkflowDoc.TryParse(rawBody, out var doc))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("body is neither canonical JSON nor a YAML view"));
            }

            var etag = request.Headers.IfMatch.ToString();
            var forceEnabled = doc.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var outcome = await store.PutAsync(id, doc, string.IsNullOrEmpty(etag) ? "*" : etag, actor!, reason!, forceEnabled, ct);
            return outcome.Outcome switch
            {
                WorkflowDefinitionOutcome.Stored => Results.Ok(outcome.View!.Summary),
                WorkflowDefinitionOutcome.Conflict => Results.Problem(ProblemErrors.Create(
                    "version-conflict", "Definition changed", StatusCodes.Status412PreconditionFailed, outcome.Detail ?? "If-Match failed")),
                WorkflowDefinitionOutcome.TooLarge => Results.Problem(ProblemErrors.ConfigInvalid(outcome.Detail ?? "too large")),
                WorkflowDefinitionOutcome.Invalid => Invalid(outcome),
                _ => Results.Problem(ProblemErrors.Create(
                    "definition-invalid", "Workflow definition invalid", StatusCodes.Status422UnprocessableEntity, outcome.Detail ?? "")),
            };
        });

        group.MapPost("/definitions/validate", async (
            HttpRequest request,
            WorkflowDefinitionValidator validator,
            CancellationToken ct) =>
        {
            var rawBody = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            if (!WorkflowDoc.TryParse(rawBody, out var doc))
            {
                return Results.Ok(new { ok = false, errors = new[] { new { code = "unparseable", detail = "body is neither canonical JSON nor a YAML view" } } });
            }

            var result = validator.Validate(doc);
            return Results.Ok(new { ok = result.Ok, errors = result.Errors });
        });

        // Shadow reconciliation: the plane's would-do step-logs diffed
        // against the arm's recorded effects, keyed on the delivery
        // identity — the strangler gate before a swap.
        group.MapGet("/shadow/report", async (
            WorkflowShadowReport report,
            HttpRequest request,
            CancellationToken ct) =>
        {
            DateTime since = DateTime.UtcNow.AddDays(-7);
            var raw = request.Query["since"].FirstOrDefault();
            if (raw is { Length: > 0 } && !DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out since))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("since must be an ISO-8601 timestamp"));
            }

            return Results.Ok(await report.ReportAsync(since, ct));
        });

        group.MapPost("/definitions/{id}/versions/{version:int}/toggle", async (
            string id,
            int version,
            JsonElement body,
            HttpRequest request,
            HttpContext http,
            WorkflowDefinitionStore store,
            WorkflowSubscriptionRegistry subscriptions,
            CancellationToken ct) =>
        {
            var actor = Mutation.RequireActor(http, request, out var reason, out var problem);
            if (problem is not null)
            {
                return problem;
            }

            var enabled = body.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True
                ? true
                : body.TryGetProperty("enabled", out var f) && f.ValueKind == JsonValueKind.False ? false : (bool?)null;
            var shadow = body.TryGetProperty("shadow", out var s) && s.ValueKind == JsonValueKind.True
                ? true
                : body.TryGetProperty("shadow", out var t) && t.ValueKind == JsonValueKind.False ? false : (bool?)null;
            if (enabled is null && shadow is null)
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("body must carry enabled and/or shadow"));
            }

            var outcome = await store.ToggleAsync(id, version, enabled, shadow, actor!, reason!, ct);
            if (outcome.Outcome == WorkflowDefinitionOutcome.Stored)
            {
                // Admin-toggle law: the subscription set refreshes NOW, so
                // a freshly enabled definition triggers on the very next
                // event (not after a TTL).
                await subscriptions.RebuildAsync(ct);
            }

            return outcome.Outcome switch
            {
                WorkflowDefinitionOutcome.Stored => Results.Ok(outcome.View!.Summary),
                WorkflowDefinitionOutcome.NotFound => Results.Problem(ProblemErrors.Create(
                    "definition-not-found", "Workflow definition not found", StatusCodes.Status404NotFound, outcome.Detail ?? "")),
                _ => Invalid(outcome),
            };
        });

        return app;
    }

    // 422 with the structured findings attached (the editor renders them per field).
    private static IResult Invalid(WorkflowDefinitionMutationResult outcome)
    {
        var problem = ProblemErrors.Create(
            outcome.Errors?.FirstOrDefault()?.Code ?? "definition-invalid",
            "Workflow definition invalid", StatusCodes.Status422UnprocessableEntity,
            outcome.Detail ?? "invalid");
        problem.Extensions["errors"] = outcome.Errors ?? [];
        return Results.Problem(problem);
    }

    /// <summary>Shared mutation law: an operator actor, a non-empty
    /// <c>X-Change-Reason</c>, and nothing else — every workflow mutation carries
    /// both, like the admin config surface.</summary>
    internal static class Mutation
    {
        public static string? RequireActor(HttpContext http, HttpRequest request, out string? reason, out IResult? problem)
        {
            reason = request.Headers.TryGetValue("X-Change-Reason", out var values) ? values.ToString() : null;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            {
                problem = Results.Problem(ProblemErrors.ConfigInvalid("the X-Change-Reason header is required (non-empty, <= 512 chars)"));
                return null;
            }

            problem = null;
            return http.GetFacilitatorPrincipal()?.Actor ?? "http-operator";
        }
    }
}
