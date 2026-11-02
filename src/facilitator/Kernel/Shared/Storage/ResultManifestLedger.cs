using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>Outcome of <see cref="IResultManifestLedger.RecordAsync"/>.</summary>
public enum ResultManifestOutcome
{
    /// <summary>A new chain head was recorded.</summary>
    Recorded,

    /// <summary>The same artifact + digest is already the chain head (or raced); no effect.</summary>
    Duplicate,

    /// <summary>Stored bytes are non-canonical, hash elsewhere than the declared digest, or the document contradicts its envelope.</summary>
    DigestMismatch,

    /// <summary>The manifest artifact row is absent, unverified, off-team, unreadable, or the work is unknown (non-disclosure).</summary>
    ArtifactMissing,

    /// <summary>An <c>outputs[].artifactId</c> does not resolve to a verified, same-team artifact carrying the declared digest.</summary>
    OutputUnresolvable,
}

/// <summary>Read projection of one <c>facilitator.result_manifests</c> row.</summary>
/// <param name="ManifestId">The manifest id (ledger PK).</param>
/// <param name="WorkId">The owning work.</param>
/// <param name="AssignmentId">The assignment the chain belongs to.</param>
/// <param name="ExecutionId">The execution the completed event declared.</param>
/// <param name="Revision">1-based chain revision.</param>
/// <param name="RevisionOf">Superseded predecessor manifest id; null on the first revision.</param>
/// <param name="Digest">Verified <c>sha256:&lt;64hex&gt;</c> of the canonical manifest document.</param>
/// <param name="StructuredDigest">Digest of the canonical <c>structuredResult</c> sub-document, when present.</param>
/// <param name="ArtifactId">The artifact row carrying the manifest bytes.</param>
/// <param name="ResultState">missing | available | validated | rejected | superseded.</param>
/// <param name="State">recorded | superseded.</param>
/// <param name="RecordedAt">Record timestamp.</param>
public sealed record ResultManifestView(
    string ManifestId,
    Guid WorkId,
    Guid AssignmentId,
    string ExecutionId,
    int Revision,
    string? RevisionOf,
    string Digest,
    string? StructuredDigest,
    Guid ArtifactId,
    string ResultState,
    string State,
    DateTime RecordedAt);

/// <summary>The result-manifest ledger: one short transaction per recorded manifest.</summary>
public interface IResultManifestLedger
{
    /// <summary>
    /// Record the manifest bytes stored under <paramref name="manifestArtifactId"/> against the
    /// assignment chain. <paramref name="envelopeData"/> is the completed event's <c>data</c>
    /// region — the source of the wire <c>resultManifest.manifestId</c> the stored document must
    /// agree with. Every verification (canonical-form, digest recompute, output cross-check) runs
    /// against the STORED bytes, never against anything the producer restates.
    /// </summary>
    Task<ResultManifestOutcome> RecordAsync(
        JsonElement envelopeData,
        Guid manifestArtifactId,
        string wireDigest,
        Guid workId,
        Guid assignmentId,
        string executionId,
        EffectivePrincipal? principal,
        CancellationToken ct);

    /// <summary>Work-scoped chain, newest revision first: null (⇒ 404) when the work is unknown or foreign to the reader.</summary>
    Task<IReadOnlyList<ResultManifestView>?> ListForWorkAsync(
        Guid workId, IReadOnlyList<string> groups, IReadOnlySet<string> roles, CancellationToken ct);

    /// <summary>The stored, verified manifest document bytes re-parsed — the
    /// completed-manifest reader's view of the just-recorded chain
    /// head (stored bytes are the law; the wire restatement never is). Null
    /// when the artifact is absent, unverified, or unreadable.</summary>
    Task<JsonDocument?> ReadStoredAsync(Guid manifestArtifactId, CancellationToken ct);
}

/// <summary>Ledger-backed <see cref="IResultManifestLedger"/>.</summary>
public sealed class ResultManifestLedger(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider,
    IArtifactStore store,
    KernelMetrics metrics) : IResultManifestLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ResultManifestOutcome> RecordAsync(
        JsonElement envelopeData,
        Guid manifestArtifactId,
        string wireDigest,
        Guid workId,
        Guid assignmentId,
        string executionId,
        EffectivePrincipal? principal,
        CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var origin = principal is null ? "rest" : "link";

        // The work decides team scope (non-disclosure: an unknown work is the
        // same 404-shaped answer as a foreign or missing artifact).
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new { w.Version, TeamSlug = w.ProjectId == null ? null : w.Project!.Team!.Slug })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return ResultManifestOutcome.ArtifactMissing;
        }

        // The manifest artifact must be a verified, same-team, kernel-readable row.
        // A work with no project has no team lineage, so the strict same-team
        // equality below can never be satisfied: the answer degrades exactly
        // like a foreign team's — the same 404-shaped ArtifactMissing
        // (non-disclosure), never a crash.
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArtifactId == manifestArtifactId, ct);
        if (artifact is null
            || artifact.State != "stored"
            || artifact.Verification != "verified"
            || !string.Equals(artifact.TeamSlug, work.TeamSlug, StringComparison.Ordinal))
        {
            return ResultManifestOutcome.ArtifactMissing;
        }

        // The stored bytes are the law: re-read them, require canonical form,
        // and recompute the digest — the wire declaration is only ever checked
        // against the store, never trusted.
        var stored = await store.GetAsync(ArtifactKeys.RelativePathOf(artifact.Digest["sha256:".Length..]), ct);
        if (stored is null)
        {
            return ResultManifestOutcome.ArtifactMissing;
        }

        JsonDocument doc;
        byte[] bytes;
        await using (stored)
        {
            using var buffer = new MemoryStream();
            await stored.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            metrics.ManifestDigestMismatch();
            return ResultManifestOutcome.DigestMismatch;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("manifestId", out var midEl)
                || midEl.ValueKind != JsonValueKind.String
                || midEl.GetString() is not { Length: > 0 } storedManifestId)
            {
                metrics.ManifestDigestMismatch();
                return ResultManifestOutcome.DigestMismatch;
            }

            // Canonical-form law: the uploaded bytes must BE the canonical
            // rendering, so the stored digest is a pure function of content.
            if (!bytes.AsSpan().SequenceEqual(ApprovalDigest.CanonicalBytes(root)))
            {
                metrics.ManifestDigestMismatch();
                return ResultManifestOutcome.DigestMismatch;
            }

            var computed = ApprovalDigest.CanonicalDocument(root);
            if (!string.Equals(computed, wireDigest, StringComparison.OrdinalIgnoreCase))
            {
                metrics.ManifestDigestMismatch();
                return ResultManifestOutcome.DigestMismatch;
            }

            // The document must agree with its envelope: the stored manifestId
            // equals the inline resultManifest.manifestId the producer declared.
            if (!envelopeData.TryGetProperty("resultManifest", out var inline)
                || inline.ValueKind != JsonValueKind.Object
                || !inline.TryGetProperty("manifestId", out var wireMid)
                || wireMid.ValueKind != JsonValueKind.String
                || !string.Equals(wireMid.GetString(), storedManifestId, StringComparison.Ordinal))
            {
                metrics.ManifestDigestMismatch();
                return ResultManifestOutcome.DigestMismatch;
            }

            // Chain law: serialize against a concurrent correction by locking
            // the current head row for this assignment.
            var head = await db.ResultManifests
                .FromSql($"SELECT * FROM facilitator.result_manifests WHERE assignment_id = {assignmentId} AND state = 'recorded' ORDER BY revision DESC LIMIT 1 FOR UPDATE")
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);
            if (head is not null
                && head.ArtifactId == manifestArtifactId
                && string.Equals(head.Digest, wireDigest, StringComparison.OrdinalIgnoreCase))
            {
                return ResultManifestOutcome.Duplicate;
            }

            // Output cross-check: every declared output resolves to a verified,
            // same-team artifact row whose stored digest equals the declaration.
            // externalRefs and the rest of the document stay opaque (recorded,
            // never fetched).
            var outputIds = new List<string>();
            if (root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var output in outputs.EnumerateArray())
                {
                    if (output.ValueKind != JsonValueKind.Object
                        || !output.TryGetProperty("artifactId", out var oidEl)
                        || oidEl.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(oidEl.GetString(), out var outputId))
                    {
                        return ResultManifestOutcome.OutputUnresolvable;
                    }

                    var declared = output.TryGetProperty("digest", out var dEl) && dEl.ValueKind == JsonValueKind.String
                        ? dEl.GetString()
                        : null;
                    var target = await db.Artifacts.AsNoTracking()
                        .Where(a => a.ArtifactId == outputId)
                        .Select(a => new { a.State, a.Verification, a.TeamSlug, a.Digest })
                        .FirstOrDefaultAsync(ct);
                    if (target is null
                        || target.State != "stored"
                        || target.Verification != "verified"
                        || !string.Equals(target.TeamSlug, work.TeamSlug, StringComparison.Ordinal)
                        || declared is null
                        || !string.Equals(target.Digest, declared, StringComparison.OrdinalIgnoreCase))
                    {
                        return ResultManifestOutcome.OutputUnresolvable;
                    }

                    outputIds.Add(outputId.ToString());
                }
            }

            var revision = head is null ? 1 : head.Revision + 1;
            if (head is not null)
            {
                // Conditional flip (race law): the head we locked must still be
                // recorded when we supersede it.
                var flipped = await db.ResultManifests
                    .Where(m => m.ManifestId == head.ManifestId && m.State == "recorded")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.State, "superseded")
                        .SetProperty(m => m.ResultState, "superseded"), ct);
                if (flipped == 0)
                {
                    return ResultManifestOutcome.Duplicate;
                }
            }

            var actor = principal is null
                ? "kernel"
                : principal.Delegated?.Subject ?? $"link:{principal.Workload.EndpointId}";
            var structuredDigest = root.TryGetProperty("structuredResult", out var sr) && sr.ValueKind == JsonValueKind.Object
                ? ApprovalDigest.CanonicalDocument(sr)
                : null;
            var digest = wireDigest.ToLowerInvariant();

            db.ResultManifests.Add(new ResultManifestEntity
            {
                ManifestId = storedManifestId,
                WorkId = workId,
                AssignmentId = assignmentId,
                ExecutionId = executionId,
                Revision = revision,
                RevisionOf = head?.ManifestId,
                Digest = digest,
                StructuredDigest = structuredDigest,
                ArtifactId = manifestArtifactId,
                OutputArtifactIds = [.. outputIds],
                ResultState = "available",
                State = "recorded",
                RecordedAt = now,
            });
            audit.Append(db, new AuditRecord(
                Actor: actor,
                Action: "manifest.recorded",
                Resource: $"manifest/{storedManifestId}",
                Decision: "allow",
                Reason: origin,
                EvidenceRef: null,
                PolicyVersion: "manifest-v1",
                Detail: Serialize(new Dictionary<string, string?>
                {
                    ["artifactId"] = manifestArtifactId.ToString(),
                    ["digest"] = digest,
                    ["revision"] = revision.ToString(),
                })));
            AppendTimeline(db, workId, work.Version, "manifest.recorded", actor, new Dictionary<string, string?>
            {
                ["manifestId"] = storedManifestId,
                ["revision"] = revision.ToString(),
                ["digest"] = digest,
            }, storedManifestId, now);

            if (head is not null)
            {
                audit.Append(db, new AuditRecord(
                    Actor: actor,
                    Action: "manifest.superseded",
                    Resource: $"manifest/{head.ManifestId}",
                    Decision: "allow",
                    Reason: "superseded",
                    EvidenceRef: null,
                    PolicyVersion: "manifest-v1",
                    Detail: Serialize(new Dictionary<string, string?>
                    {
                        ["supersededBy"] = storedManifestId,
                        ["revision"] = head.Revision.ToString(),
                    })));
                AppendTimeline(db, workId, work.Version, "manifest.superseded", actor, new Dictionary<string, string?>
                {
                    ["manifestId"] = head.ManifestId,
                    ["revision"] = head.Revision.ToString(),
                    ["supersededBy"] = storedManifestId,
                }, head.ManifestId, now);
                metrics.ManifestSuperseded();
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // PK collision: the same manifestId re-recorded under a
                // different artifact — one document, one row.
                await tx.RollbackAsync(ct);
                return ResultManifestOutcome.Duplicate;
            }

            await tx.CommitAsync(ct);
            metrics.ManifestRecorded(origin);
            return ResultManifestOutcome.Recorded;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResultManifestView>?> ListForWorkAsync(
        Guid workId, IReadOnlyList<string> groups, IReadOnlySet<string> roles, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new { w.ProjectId, TeamSlug = w.ProjectId == null ? null : w.Project!.Team!.Slug })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return null;
        }

        // Work-scope 404 law: a foreign team's work is indistinguishable from
        // an unknown one; a team-less work (no project) reads only to an admin.
        if (!roles.Contains("framework-admin")
            && (work.TeamSlug is null || !groups.Contains($"/teams/{work.TeamSlug}")))
        {
            return null;
        }

        var rows = await db.ResultManifests.AsNoTracking()
            .Where(m => m.WorkId == workId)
            .OrderByDescending(m => m.Revision)
            .ToListAsync(ct);
        return rows
            .Select(m => new ResultManifestView(
                m.ManifestId, m.WorkId, m.AssignmentId, m.ExecutionId, m.Revision, m.RevisionOf,
                m.Digest, m.StructuredDigest, m.ArtifactId, m.ResultState, m.State, m.RecordedAt))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<JsonDocument?> ReadStoredAsync(Guid manifestArtifactId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArtifactId == manifestArtifactId, ct);
        if (artifact is null || artifact.State != "stored" || artifact.Verification != "verified")
        {
            return null;
        }

        await using var stored = await store.GetAsync(
            ArtifactKeys.RelativePathOf(artifact.Digest["sha256:".Length..]), ct);
        if (stored is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendTimeline(
        FacilitatorDbContext db, Guid workId, long workVersion, string kind, string actor,
        Dictionary<string, string?> detail, string correlationId, DateTime now) =>
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = workVersion,
            Kind = kind,
            Actor = actor,
            Detail = Serialize(detail),
            CorrelationId = correlationId,
            CreatedAt = now,
        });

    private static string Serialize(Dictionary<string, string?> detail) =>
        JsonSerializer.Serialize(detail, Json);
}
