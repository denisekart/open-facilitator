using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.AspNetCore.Mvc;

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
public sealed class ResultIngress(IResultManifestLedger ledger)
{
    /// <summary>The event type this arm handles on both planes.</summary>
    public const string CompletedType = "org.facilitator.event.assignment.completed.v1";

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

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The final <c>'/'</c>-segment of the <c>artifact://</c> URI (the kernel-side artifact id).</summary>
    private static string LastSegment(string artifactUri)
    {
        var slash = artifactUri.LastIndexOf('/');
        return slash < 0 ? artifactUri : artifactUri[(slash + 1)..];
    }
}
