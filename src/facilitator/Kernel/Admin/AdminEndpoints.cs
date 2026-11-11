using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Admin;

/// <summary>
/// <c>/api/admin/*</c>: the operator read/write surface
/// the embedded inspector consumes. Endpoints stay thin like
/// <c>MapIdentityApi</c> — parse, delegate to the stores, map. Authorization
/// is 100% config-driven (<c>admin.read</c>/<c>admin.mutate</c> bindings in
/// <c>identity:authorization:operations</c>): NO <c>.RequireAuthorization</c>
/// anywhere, and every route MUST carry a binding, or the gate admits every
/// authenticated principal.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Map the admin group (mount after <c>MapIdentityApi</c>).</summary>
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/summary", async (AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(await stores.SummaryAsync(ct)));

        group.MapGet("/search", async (string? q, string? type, int? limit, AdminReadStores stores, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Problem(ProblemErrors.SearchQueryRequired());
            }

            return Results.Ok(await stores.SearchAsync(q, type, limit ?? 10, ct));
        });

        group.MapGet("/projects/{id:guid}", async (Guid id, AdminReadStores stores, CancellationToken ct) =>
        {
            var detail = await stores.ProjectAsync(id, ct);
            return detail is null
                ? Results.Problem(ProblemErrors.Create("project-not-found", "Project not found", StatusCodes.Status404NotFound, $"no project {id}"))
                : Results.Ok(detail);
        });

        group.MapGet("/work/{id:guid}", async (Guid id, AdminReadStores stores, CancellationToken ct) =>
        {
            var detail = await stores.WorkAsync(id, ct);
            return detail is null
                ? Results.Problem(ProblemErrors.WorkNotFound(id))
                : Results.Ok(detail);
        });

        // Same WorkEventRow[] shape as GET /api/works/{id}/timeline — the
        // admin surface reuses the coordination store call verbatim.
        group.MapGet("/work/{id:guid}/timeline", async (Guid id, Work.ICoordinationStore store, CancellationToken ct) =>
            Results.Ok((await store.TimelineAsync(id, ct)).ToArray()));

        group.MapGet("/links", async (AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(await stores.LinksAsync(ct)));

        group.MapGet("/deliveries", async (string? state, int? limit, AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(new { items = await stores.DeliveriesAsync(state, limit ?? 50, ct) }));

        group.MapGet("/provisioning", async (AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(new { items = await stores.ProvisioningAsync(ct) }));

        group.MapGet("/metrics", async (string? window, AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(await stores.MetricsAsync(window, ct)));

        group.MapGet("/replicas", async (AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(await stores.ReplicasAsync(ct)));

        // Audit trail read (the chat history screen's evidence surface); the
        // admin.read binding covers it via /api/admin/audit.
        group.MapGet("/audit", async (string? action, string? actor, DateTime? since, int? limit, AdminReadStores stores, CancellationToken ct) =>
            Results.Ok(await stores.AuditAsync(action, actor, since, limit ?? 50, ct)));

        // H3/N15: the export leg - a bounded window, no raw credentials, and
        // a retention policy the operator can read (admin_config 'audit-policy').
        group.MapGet("/audit/export", async (
            DateTime? from, DateTime? to, string? team, string? format, AdminReadStores stores, CancellationToken ct) =>
        {
            var rows = await stores.AuditExportAsync(from, to, team, ct);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = new StringBuilder("occurred_at,actor,action,resource,decision,reason\n");
                foreach (var row in rows)
                {
                    csv.Append(string.Join(',', new[]
                    {
                        row.OccurredAt.ToString("O"), row.Actor, row.Action, row.Resource, row.Decision, row.Reason,
                    }.Select(CsvField))).Append('\n');
                }

                return Results.Text(csv.ToString(), "text/csv");
            }

            return Results.Ok(rows);
        });

        // I2: force a sweeper pass without waiting for the loop.
        group.MapPost("/sweeps/{name}", async (
            string name,
            Work.NudgeSweeper nudge,
            Work.ApprovalSweeper approvals,
            Work.ScheduleSweeper schedules,
            Work.SessionLossSweeper sessionLoss,
            IDbContextFactory<FacilitatorDbContext> contextFactory,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var fired = name switch
            {
                "nudge" => await nudge.SweepOnceAsync(ct),
                "approvals" => await approvals.SweepOnceAsync(ct),
                "schedules" => await schedules.SweepOnceAsync(ct),
                "session-loss" => await sessionLoss.SweepOnceAsync(ct),
                "audit-retention" => await AuditRetentionSweep.PruneAsync(contextFactory, timeProvider, ct),
                _ => -1,
            };
            return fired < 0
                ? Results.Problem(ProblemErrors.Create(
                    "unknown-sweep", "Bad request", StatusCodes.Status400BadRequest,
                    "sweep name must be nudge|approvals|schedules|session-loss|audit-retention"))
                : Results.Ok(new { sweep = name, effects = fired });
        });

        MapConfig(group);
        MapMutations(group);

        return app;
    }

    // ---- config surface (true If-Match optimistic revision) ----

    // CSV field law: quote everything that could carry a delimiter, newline
    // or quote; no raw credential ever enters an audit row in the first place.
    private static string CsvField(string? value) =>
        value is null
            ? string.Empty
            : value.Any(c => c is ',' or '"' or '\n' or '\r')
                ? '"' + value.Replace("\"", "\"\"") + '"'
                : value;

    private static void MapConfig(RouteGroupBuilder group)
    {
        group.MapGet("/config", async (AdminConfigStore store, CancellationToken ct) =>
        {
            var stored = await store.ListAsync(ct);
            var storedByResource = stored.ToDictionary(r => r.Resource, r => r.RowVersion);
            return Results.Ok(AdminConfigStore.Resources
                .Select(r => new { resource = r, exists = storedByResource.ContainsKey(r), rowVersion = storedByResource.GetValueOrDefault(r) })
                .ToArray());
        });

        group.MapGet("/config/{resource}", async (string resource, AdminConfigStore store, HttpContext http, CancellationToken ct) =>
        {
            var view = await store.GetAsync(resource, ct);
            if (view is null)
            {
                return AdminConfigStore.Resources.Contains(resource, StringComparer.Ordinal)
                    ? Results.Problem(ProblemErrors.Create("config-not-set", "Config not set", StatusCodes.Status404NotFound,
                        $"resource '{resource}' has never been written"))
                    : Results.Problem(ProblemErrors.ConfigResourceUnknown(resource));
            }

            http.Response.Headers.ETag = $"\"{view.RowVersion}\"";
            return Results.Ok(view);
        });

        group.MapGet("/config/{resource}/history", async (string resource, int? limit, AdminConfigStore store, CancellationToken ct) =>
            Results.Ok(await store.GetHistoryAsync(resource, Math.Clamp(limit ?? 20, 1, 20), ct)));

        group.MapPatch("/config/{resource}", async (string resource, HttpRequest request, AdminConfigStore store, HttpContext http, CancellationToken ct) =>
        {
            var author = http.GetFacilitatorPrincipal()?.Actor ?? "http-operator";
            var reason = request.Headers.TryGetValue("X-Change-Reason", out var reasonValues) ? reasonValues.ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("the X-Change-Reason header is required (non-empty, <= 512 chars)"));
            }

            if (!TryParseIfMatch(request.Headers.IfMatch, out var expectedVersion))
            {
                return Results.Problem(ProblemErrors.ConfigPreconditionMissing(resource));
            }

            var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            JsonElement payload;
            try
            {
                payload = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.ConfigInvalid("body must be a JSON object document"));
            }

            var result = await store.PatchAsync(resource, payload, expectedVersion, author, reason, ct);
            switch (result.Outcome)
            {
                case AdminConfigOutcome.Stored:
                    http.Response.Headers.ETag = $"\"{result.View!.RowVersion}\"";
                    return Results.Ok(result.View);
                case AdminConfigOutcome.UnknownResource:
                    return Results.Problem(ProblemErrors.ConfigResourceUnknown(resource));
                case AdminConfigOutcome.Invalid:
                    return Results.Problem(ProblemErrors.ConfigInvalid(result.Detail ?? "payload violates the resource shape"));
                case AdminConfigOutcome.TooLarge:
                    return Results.Problem(ProblemErrors.ConfigPayloadTooLarge(result.Detail ?? "payload too large"));
                default:
                    return Results.Problem(ProblemErrors.ConfigVersionConflict(resource, result.View?.RowVersion ?? 0));
            }
        });
    }

    // If-Match: '*' forces; a quoted integer ETag fences; absent/malformed →
    // 428 (the law: config writes always state the revision they saw).
    private static bool TryParseIfMatch(string? ifMatch, out long? expectedVersion)
    {
        expectedVersion = null;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }

        var header = ifMatch.Trim();
        if (header == "*")
        {
            return true;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        if (!long.TryParse(header.Trim('"'), out var parsed) || parsed <= 0)
        {
            return false;
        }

        expectedVersion = parsed;
        return true;
    }

    // ---- mutations (dead-letter replay, manifest re-mark) ----

    private static void MapMutations(RouteGroupBuilder group)
    {
        group.MapPost("/dead-letters/{deadLetterId:guid}/replay", async (Guid deadLetterId, OutboxAdminStore admin, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await admin.ReplayAsync(deadLetterId, http.GetFacilitatorPrincipal()?.Actor, ct);
            return outcome switch
            {
                OutboxRepository.ReplayOutcome.Replayed => Results.Ok(new { deadLetterId, replayed = true }),
                OutboxRepository.ReplayOutcome.AlreadyReplayed => Results.Problem(ProblemErrors.Create("version-conflict", "Version conflict",
                    StatusCodes.Status409Conflict, $"dead letter {deadLetterId} was already replayed — the ledger holds exactly one replay command")),
                OutboxRepository.ReplayOutcome.NotFound => Results.Problem(ProblemErrors.DeadLetterNotFound(deadLetterId)),
                _ => Results.Problem(ProblemErrors.ReplayEnvelopeInvalid(
                    $"the snapshot envelope of dead letter {deadLetterId} fails contract validation — a replay never edits the payload")),
            };
        });
        group.MapPost("/manifests/{manifestId}/re-mark", async (string manifestId, HttpRequest request, AdminMutationStore mutations, HttpContext http, CancellationToken ct) =>
        {
            var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            string? target;
            try
            {
                using var document = JsonDocument.Parse(body);
                target = document.RootElement.TryGetProperty("resultState", out var s) ? s.GetString() : null;
            }
            catch (JsonException)
            {
                return Results.Problem(ProblemErrors.ManifestReMarkInvalid("body must be {\"resultState\": \"available\"|\"validated\"|\"rejected\"}"));
            }

            if (target is not ("available" or "validated" or "rejected"))
            {
                return Results.Problem(ProblemErrors.ManifestReMarkInvalid(
                    $"'{target}' is not human-writable (missing/superseded are chain-law owned)"));
            }

            var outcome = await mutations.ReMarkManifestAsync(manifestId, target, http.GetFacilitatorPrincipal()?.Actor, ct);
            return outcome == ReMarkOutcome.Stored
                ? Results.Ok(new { manifestId, resultState = target })
                : Results.Problem(ProblemErrors.ManifestNotFound(manifestId));
        });
    }
}
