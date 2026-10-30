using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>What the manifest arm hands back to the ingress that invoked it.</summary>
/// <param name="Problem">Typed problem to answer with; null = handled.</param>
/// <param name="Status">Outcome status wire value when handled.</param>
public sealed record ResultArm(ProblemDetails? Problem, string Status)
{
    /// <summary>True when the arm refused — the caller must NOT settle the inbox as processed.</summary>
    public bool Refused => Problem is not null;
}

/// <summary>
/// The result-manifest arm of <c>event.assignment.completed.v1</c> (Plan 15),
/// shared by both ingress surfaces. A manifest-less completed event stays the
/// plain accepted event it always was (the legacy inline path); once
/// <c>data.resultManifestRef</c> rides along, the referenced artifact's stored
/// bytes are recorded into the manifest ledger — every verification runs
/// against the store, and any unresolvable identity answers the same 404
/// (work-scope non-disclosure, the approvals precedent).
/// </summary>
public sealed class ResultIngress(
    IResultManifestLedger ledger,
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    ProviderRegistry providers,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider)
{
    /// <summary>The completed event type this arm handles on both planes.</summary>
    public const string CompletedType = "org.facilitator.event.assignment.completed.v1";

    /// <summary>Capability of the manifest-dispatched PR creation op (the
    /// connector's op id; the gateway's policy routes it to the team's
    /// connector unit).</summary>
    public const string PrCreateCapability = "vcs.change_request.create";

    /// <summary>Capability of the manifest-dispatched PR comment op.</summary>
    public const string PrCommentCapability = "vcs.change_request.comment";

    /// <summary>Event arm: record the manifest the completed event points at, when it points at one.</summary>
    public async Task<ResultArm> HandleCompletedAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var wireRef = GetString(data, "resultManifestRef");
        if (wireRef is null)
        {
            // Legacy inline-only completion: today's behavior, no ledger effect.
            return new ResultArm(null, "accepted");
        }

        var digest = GetString(data, "resultDigest") ?? string.Empty;
        var workRaw = GetString(data, "workId");
        var assignmentRaw = GetString(data, "assignmentId");
        var executionId = GetString(data, "executionId") ?? string.Empty;
        if (Guid.TryParse(workRaw, out var workId)
            && Guid.TryParse(assignmentRaw, out var assignmentId)
            && Guid.TryParse(LastSegment(wireRef), out var manifestArtifactId))
        {
            var outcome = await ledger.RecordAsync(
                data, manifestArtifactId, digest, workId, assignmentId, executionId, principal, ct);
            if (outcome is ResultManifestOutcome.Recorded or ResultManifestOutcome.Duplicate)
            {
                await DispatchManifestRequestsAsync(
                    data, manifestArtifactId, digest, workId, assignmentId, assignmentRaw!, ct);
            }

            return outcome switch
            {
                ResultManifestOutcome.Recorded or ResultManifestOutcome.Duplicate => new ResultArm(null, "accepted"),
                ResultManifestOutcome.ArtifactMissing => new(ProblemErrors.ManifestNotFound(wireRef), "manifest-not-found"),
                ResultManifestOutcome.DigestMismatch => new(ProblemErrors.ManifestDigestMismatch(wireRef), "manifest-digest-mismatch"),
                _ => new(ProblemErrors.ManifestOutputUnresolvable(wireRef), "manifest-output-unresolvable"),
            };
        }

        // Unknown work, foreign assignment identity space, or a non-GUID ref
        // segment: the same 404 the ledger answers for a foreign artifact.
        return new ResultArm(ProblemErrors.ManifestNotFound(wireRef), "manifest-not-found");
    }

    /// <summary>Plan 24(1d): the manifest reader. A RECORDED manifest for an
    /// assignment whose CAPABILITY one provider configured
    /// (<c>Providers:&lt;p&gt;:PrRequestCapability</c>) may carry connector
    /// work: <c>structuredResult.prRequest</c> ⇒ one
    /// <c>vcs.change_request.create</c> assign + the <c>branch:&lt;head&gt;</c>
    /// pending-PR marker binding (24(1e)'s resolver key);
    /// <c>structuredResult.commentRequests[]</c> ⇒ one
    /// <c>vcs.change_request.comment</c> assign each (the steer-incorporated
    /// legs). The capability rides the ASSIGN envelope: no runtime table
    /// joins assignments to capabilities as-built, so the outbox row that
    /// carried the assignment IS the ledger (same resolution as the steer
    /// arm), and its recipient is the owning endpoint. Digest-keyed command
    /// ids make every leg replay-idempotent against the connector's own
    /// ledger; the unique outbox command id makes it single-shot here. The
    /// stored bytes (re-read, verified) are the source — the wire restates
    /// only location + digest.</summary>
    private async Task DispatchManifestRequestsAsync(
        JsonElement data, Guid manifestArtifactId, string digest, Guid workId, Guid assignmentId, string wireAssignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var assign = await db.Outbox.FromSql(
                $"""SELECT * FROM facilitator.outbox WHERE work_id = {workId} AND event_type = 'org.facilitator.command.work.assign.v1' AND envelope->'data'->>'assignmentId' = {wireAssignmentId} ORDER BY created_at DESC LIMIT 1""")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        if (assign is null)
        {
            return;
        }

        using var assignDoc = JsonDocument.Parse(assign.Envelope);
        var capability = assignDoc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString();
        var options = providers.All().FirstOrDefault(o =>
            o.PrRequestCapability is { Length: > 0 } cap && cap == capability);
        if (options is null)
        {
            return;
        }

        using var stored = await ledger.ReadStoredAsync(manifestArtifactId, ct);
        if (stored?.RootElement.TryGetProperty("structuredResult", out var structured) is not true
            || structured.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var objective = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => w.Objective).FirstOrDefaultAsync(ct) ?? string.Empty;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var dispatched = false;
        if (structured.TryGetProperty("prRequest", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            var repo = Field(pr, "repo");
            var head = Field(pr, "head");
            if (repo is { Length: > 0 } && head is { Length: > 0 })
            {
                var commandId = $"pr-create:{digest}";
                var envelope = ProviderEnvelopes.ConnectorCommand(
                    workId, PrCreateCapability, options.Provider + "-connector", options.Provider,
                    new JsonObject
                    {
                        ["objective"] = objective.Length <= 512 ? objective : objective[..512],
                        ["sourceRefs"] = new JsonArray(),
                        ["repo"] = repo,
                        ["head"] = head,
                        ["base"] = Field(pr, "base") ?? "main",
                        ["title"] = Field(pr, "title") ?? objective,
                        ["body"] = Field(pr, "body") ?? string.Empty,
                    },
                    "vcs.change_request",
                    now,
                    commandId);
                AppendDispatchRow(db, envelope, assign.RecipientEndpointId, commandId, workId, now);

                // Pending-PR marker (24(1e)): the PR number is unknown at
                // manifest time, so the binding stands keyed by the head
                // branch until the provider's pull_request event resolves
                // it onto the PR number.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO facilitator.communication_bindings
                         (binding_id, provider, channel, root_message_id, work_id, idempotency_key, created_at)
                     VALUES (gen_random_uuid(), {options.Provider}, {repo}, {"branch:" + head},
                             {workId}, {"pr-request:" + digest}, {now})
                     ON CONFLICT DO NOTHING
                     """, ct);
                dispatched = true;
            }
        }

        if (structured.TryGetProperty("commentRequests", out var comments) && comments.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var request in comments.EnumerateArray())
            {
                var repo = Field(request, "repo");
                var issue = Field(request, "issue");
                var body = Field(request, "body");
                if (request.ValueKind == JsonValueKind.Object
                    && repo is { Length: > 0 } && issue is { Length: > 0 } && body is { Length: > 0 })
                {
                    var commandId = $"pr-comment:{digest}:{index}";
                    var envelope = ProviderEnvelopes.ConnectorCommand(
                        workId, PrCommentCapability, options.Provider + "-connector", options.Provider,
                        new JsonObject { ["repo"] = repo, ["issue"] = issue, ["body"] = body },
                        "vcs.change_request.comment",
                        now,
                        commandId);
                    AppendDispatchRow(db, envelope, assign.RecipientEndpointId, commandId, workId, now);
                    dispatched = true;
                }

                index++;
            }
        }

        if (!dispatched)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Replay raced a concurrent delivery of the same completed
            // event (unique command id / unique marker row): the first
            // writer's rows stand, this one is the duplicate.
        }
    }

    private void AppendDispatchRow(
        FacilitatorDbContext db, JsonObject envelope, string recipient, string commandId, Guid workId, DateTime now)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            RecipientEndpointId = recipient,
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            CommandId = commandId,
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    private static string? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The final <c>'/'</c>-segment of the <c>artifact://</c> URI (the kernel-side artifact id).</summary>
    private static string LastSegment(string artifactUri)
    {
        var slash = artifactUri.LastIndexOf('/');
        return slash < 0 ? artifactUri : artifactUri[(slash + 1)..];
    }
}
