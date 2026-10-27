using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Work;

/// <summary>What an artifact arm hands back to the ingress that invoked it.</summary>
/// <param name="Problem">Typed problem to answer with; null = handled.</param>
/// <param name="Status">Outcome status wire value when handled.</param>
/// <param name="Ticket">Upload ticket to attach to the ack (request_upload only).</param>
public sealed record ArtifactArm(ProblemDetails? Problem, string Status, ArtifactTicket? Ticket = null)
{
    /// <summary>True when the arm refused — the caller must NOT settle the inbox as processed.</summary>
    public bool Refused => Problem is not null;
}

/// <summary>
/// The artifact-family arms shared by both ingress surfaces:
/// <c>command.artifact.request_upload</c> allocates a pending slot through the
/// ledger (the admission gate already evaluated the evidence + audited the
/// link principal) and the ticket rides back IN-BAND on the ack;
/// <c>command.artifact.confirm_upload</c> re-hashes bytes a producer put
/// DIRECTLY at the store (the link token is the actor proof — no
/// actorEvidenceRef needed, matching request_upload's own handling);
/// <c>event.artifact.available</c> registers external declarations (unverified)
/// or idempotently confirms kernel-stored ones. A foreign wire artifactId never
/// rewrites a kernel row — first writer owns it (a mismatched re-declaration is
/// a refused 422 + denied audit).
/// </summary>
public sealed class ArtifactIngress(IArtifactLedger ledger)
{
    /// <summary>Command arm: allocate an upload slot for a screened request_upload envelope.</summary>
    public async Task<ArtifactArm> HandleRequestUploadAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        if (!ArtifactArmPolicy.TryParse(data, out var fields, out var problem))
        {
            return new ArtifactArm(problem, "failed");
        }

        var ticket = await ledger.AllocateAsync(
            fields.MediaType, fields.Size, fields.Digest, fields.WorkId, fields.AssignmentId,
            fields.RetentionPolicy, fields.Comment, fields.CommandId,
            principal, null, "link", ct);
        return new ArtifactArm(null, "accepted", ticket);
    }

    /// <summary>Command arm: verify + settle a direct-to-store upload the producer confirmed.</summary>
    public async Task<ArtifactArm> HandleConfirmUploadAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var rawId = GetString(data, "artifactId");
        if (rawId is null || !Guid.TryParse(rawId, out var artifactId))
        {
            return new ArtifactArm(ProblemErrors.SchemaViolation("artifactId must be a GUID"), "failed");
        }

        // The link token IS the actor proof: the workload (team scope) rides
        // straight from the admitted principal.
        var workload = principal?.Workload;
        var result = await ledger.ConfirmAsync(
            artifactId, workload?.TeamSlug ?? string.Empty, workload, ct);
        return result.Outcome switch
        {
            ArtifactStoreOutcome.Stored or ArtifactStoreOutcome.Idempotent => new ArtifactArm(null, "confirmed"),
            ArtifactStoreOutcome.DigestMismatch =>
                new ArtifactArm(ProblemErrors.ArtifactDigestMismatch(artifactId.ToString()), "failed"),
            ArtifactStoreOutcome.SizeMismatch =>
                new ArtifactArm(ProblemErrors.ArtifactSizeMismatch(artifactId.ToString()), "failed"),
            ArtifactStoreOutcome.NotConfirmable =>
                new ArtifactArm(ProblemErrors.ArtifactNotConfirmable(artifactId.ToString()), "failed"),
            ArtifactStoreOutcome.NotPending =>
                new ArtifactArm(ProblemErrors.ArtifactNotPending(artifactId.ToString()), "failed"),
            _ => new ArtifactArm(ProblemErrors.ArtifactNotFound(artifactId.ToString()), "failed"),
        };
    }

    /// <summary>Event arm: record an artifact.available declaration (external or confirmation).</summary>
    public async Task<ArtifactArm> HandleEventAsync(JsonElement root, CancellationToken ct)
    {
        var result = await ledger.RegisterExternalAsync(root, ct);
        var wireId = root.GetProperty("data").GetProperty("artifactId").GetString() ?? string.Empty;
        return result.Outcome switch
        {
            ArtifactRegisterOutcome.DeclarationMismatch =>
                new ArtifactArm(ProblemErrors.ArtifactDeclarationMismatch(wireId), "failed"),
            ArtifactRegisterOutcome.Registered => new ArtifactArm(null, "registered"),
            ArtifactRegisterOutcome.Confirmed => new ArtifactArm(null, "confirmed"),
            _ => new ArtifactArm(null, "duplicate"),
        };
    }

    /// <summary>The still-live ticket a duplicate link-plane request_upload re-attaches (null once spent/expired).</summary>
    public Task<ArtifactTicket?> TicketForLinkCommandAsync(string commandId, CancellationToken ct) =>
        ledger.TicketForCommandAsync(commandId, "link", ct);

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Shared request-upload body parsing for both planes (schema-validated
/// on the wire plane; mirrored manually for the REST twin).</summary>
internal static class ArtifactArmPolicy
{
    /// <summary>Parse + validate the allocation fields. Returns false with the typed problem on any violation.</summary>
    public static bool TryParse(
        JsonElement data,
        out (string MediaType, long Size, string Digest, Guid? WorkId, Guid? AssignmentId, string? RetentionPolicy, string? Comment, string? CommandId) fields,
        out ProblemDetails? problem)
    {
        fields = default;
        problem = null;
        var mediaType = GetString(data, "mediaType");
        if (string.IsNullOrEmpty(mediaType) || mediaType!.Length > 256)
        {
            problem = ProblemErrors.SchemaViolation("mediaType (1..256 chars) is required");
            return false;
        }

        long size = -1;
        if (data.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szi))
        {
            size = szi;
        }

        if (size < 0)
        {
            problem = ProblemErrors.SchemaViolation("size (integer >= 0) is required");
            return false;
        }

        var digest = GetString(data, "digest");
        if (digest is null || !IsValidDigest(digest))
        {
            problem = ProblemErrors.SchemaViolation("digest must match sha256:[0-9a-f]{64}");
            return false;
        }

        Guid? workId = null;
        if (GetString(data, "workId") is { } workRaw)
        {
            if (!Guid.TryParse(workRaw, out var parsedWork))
            {
                problem = ProblemErrors.SchemaViolation("workId must be a GUID");
                return false;
            }

            workId = parsedWork;
        }

        Guid? assignmentId = null;
        if (GetString(data, "assignmentId") is { } asgRaw)
        {
            if (!Guid.TryParse(asgRaw, out var parsedAsg))
            {
                problem = ProblemErrors.SchemaViolation("assignmentId must be a GUID");
                return false;
            }

            assignmentId = parsedAsg;
        }

        var retention = GetString(data, "retentionPolicy");
        if (retention is not null && !IsValidRetention(retention))
        {
            problem = ProblemErrors.SchemaViolation("retentionPolicy must be \"permanent\", \"archive\", or Nd (1-9999 days, e.g. \"30d\")");
            return false;
        }

        var comment = GetString(data, "comment");
        if (comment is { Length: > 1024 })
        {
            problem = ProblemErrors.SchemaViolation("comment exceeds 1024 chars");
            return false;
        }

        fields = (mediaType!, size, digest, workId, assignmentId, retention, comment, GetString(data, "commandId"));
        return true;
    }

    /// <summary>Wire-retention grammar (mirrors the schema's anyOf exactly): permanent, archive, or Nd with 1 &lt;= N &lt;= 9999.</summary>
    public static bool IsValidRetention(string retention)
    {
        if (retention is "permanent" or "archive")
        {
            return true;
        }

        if (retention.Length is < 2 or > 5 || retention[^1] != 'd' || retention[0] is < '1' or > '9')
        {
            return false;
        }

        foreach (var c in retention.AsSpan(1, retention.Length - 2))
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Schema-locked digest grammar: sha256: + 64 lowercase hex.</summary>
    public static bool IsValidDigest(string digest)
    {
        if (digest.Length != "sha256:".Length + 64 || !digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in digest.AsSpan("sha256:".Length))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
