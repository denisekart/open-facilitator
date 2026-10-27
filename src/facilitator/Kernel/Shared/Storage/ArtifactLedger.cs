using System.Diagnostics;
using System.Text.Json;


using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
namespace Facilitator.Kernel.Shared.Storage;

/// <summary>Upload ticket handed back in-band on the ack / 201 body.</summary>
/// <param name="ArtifactId">Kernel artifact id (wire: lowercase GUID).</param>
/// <param name="UploadLocation">Kernel-relative PUT path (plane-appropriate); the producer composes it against the base URL it already talks to.</param>
/// <param name="UploadUrl">Presigned direct-upload URL when the configured adapter mints one; null = the kernel-proxied <paramref name="UploadLocation"/> is the only byte path.</param>
/// <param name="Method"><c>PUT</c> (direct, when <paramref name="UploadUrl"/> rides) or <c>POST</c> (kernel-proxied at <paramref name="UploadLocation"/>).</param>
/// <param name="ExpiresAt">When the pending allocation lapses (UTC) — the SAME instant a minted presigned URL expires (one timestamp, no drift).</param>
public sealed record ArtifactTicket(Guid ArtifactId, string UploadLocation, string? UploadUrl, string Method, DateTime ExpiresAt)
{
    /// <summary>One law for both faces of a slot: the allocation lapses at the SAME instant a minted presigned URL does — ticket expiry and presign TTL are ONE timestamp. The content route mints download URLs with this too.</summary>
    public static readonly TimeSpan AllocationTtl = TimeSpan.FromMinutes(15);
}

/// <summary>Projection of one ledger row for readers (never exposes the storage URI).</summary>
public sealed record ArtifactView(
    Guid ArtifactId,
    Guid? WorkId,
    Guid? AssignmentId,
    string MediaType,
    long Size,
    string Digest,
    string State,
    string Verification,
    string Origin,
    string CreatedBy,
    string? TeamSlug,
    DateTime CreatedAt,
    DateTime? StoredAt,
    string RetentionPolicy)
{
    /// <summary>Relative store path (kernel-internal; readers use the content endpoint).</summary>
    public string StorageUri { get; init; } = string.Empty;
}

/// <summary>Machine outcome of a content PUT.</summary>
public enum ArtifactStoreOutcome
{
    /// <summary>Bytes stored, digest verified, row pending→stored.</summary>
    Stored,

    /// <summary>Already stored with the SAME digest — idempotent re-PUT (200).</summary>
    Idempotent,

    /// <summary>Row missing, foreign-team, or wrong state — answer absent.</summary>
    NotFound,

    /// <summary>Received bytes hash elsewhere than declared — row rejected.</summary>
    DigestMismatch,

    /// <summary>Actual bytes differ from the declared size — row rejected.</summary>
    SizeMismatch,

    /// <summary>Row is stored but the declared digest names a different blob (re-declaration).</summary>
    AlreadyStoredOther,

    /// <summary>Row visible, but a direct upload cannot be confirmed for it: the configured adapter mints no URLs (filesystem: proxied PUT is the only byte path) or the row is external:/foreign-scheme.</summary>
    NotConfirmable,

    /// <summary>Row visible, same team, but not pending (rejected/deleted — the slot is spent).</summary>
    NotPending,
}

/// <summary>Result of one content PUT.</summary>
public sealed record ArtifactStoreResult(ArtifactStoreOutcome Outcome, ArtifactView? Row = null);

/// <summary>Machine outcome of external registration / kernel-stored confirmation via artifact.available.</summary>
public enum ArtifactRegisterOutcome
{
    /// <summary>External row inserted (unverified).</summary>
    Registered,

    /// <summary>Kernel-stored row confirmed — state untouched, timeline noted.</summary>
    Confirmed,

    /// <summary>Same wire id re-declared identically — no-op.</summary>
    Duplicate,

    /// <summary>Kernel row exists but mediaType/size/digest differ — refuse, row stays as recorded.</summary>
    DeclarationMismatch,
}

/// <summary>Result of one registration arm.</summary>
public sealed record ArtifactRegisterResult(ArtifactRegisterOutcome Outcome, Guid? ArtifactId = null);

/// <summary>
/// The artifacts ledger: the only writer of <c>facilitator.artifacts</c>
/// (one short transaction per operation, the ApprovalLedger template). State
/// transitions are conditional UPDATEs (the DB is the arbiter); every
/// allocation/store/reject/registration is audited same-tx; store on a row
/// with a work appends an <c>artifact.stored</c> timeline note same-tx. Team
/// scope is a column (<c>team_slug</c>): a foreign team's row answers
/// <c>null</c> everywhere (404 non-disclosure, the approvals precedent).
/// </summary>
public interface IArtifactLedger
{
    /// <summary>Allocate a pending upload slot. <paramref name="origin"/> is link|rest. Returns the ticket.</summary>
    Task<ArtifactTicket> AllocateAsync(
        string mediaType,
        long size,
        string digest,
        Guid? workId,
        Guid? assignmentId,
        string? retentionPolicy,
        string? comment,
        string? commandId,
        EffectivePrincipal? principal,
        FacilitatorPrincipal? human,
        string origin,
        CancellationToken ct);

    /// <summary>The ticket a duplicate request_upload re-attaches: only while the row is pending and un-expired.</summary>
    Task<ArtifactTicket?> TicketForCommandAsync(string commandId, string origin, CancellationToken ct);

    /// <summary>Store uploaded bytes for a pending slot: count + hash them, verify against the declaration, flip pending→stored. Foreign/missing ⇒ NotFound (404).</summary>
    Task<ArtifactStoreResult> StoreAsync(
        Guid artifactId,
        Stream content,
        string workloadOrActorTeam,
        LinkPrincipal? workload,
        CancellationToken ct);

    /// <summary>Confirm a direct-to-store upload for a pending slot: HEAD (absent ⇒ NotFound), size law, kernel re-hash law, then the same conditional-update + audit + timeline tail as the proxied PUT. The row's scheme — never live config — decides confirmability.</summary>
    Task<ArtifactStoreResult> ConfirmAsync(
        Guid artifactId,
        string workloadOrActorTeam,
        LinkPrincipal? workload,
        CancellationToken ct);

    /// <summary>Register/confirm an <c>event.artifact.available</c> declaration (external rows unverified, kernel-stored rows idempotent confirmation).</summary>
    Task<ArtifactRegisterResult> RegisterExternalAsync(JsonElement eventRoot, CancellationToken ct);

    /// <summary>One row, team-scoped: null (⇒ 404) when missing or foreign.</summary>
    Task<ArtifactView?> GetAsync(string actor, IReadOnlyList<string> groups, IReadOnlySet<string> roles, Guid artifactId, CancellationToken ct);

    /// <summary>Team-scoped row for the content endpoints: row + readable state (stored, and a scheme one configured adapter Owns). Null when missing/foreign/not-readable.</summary>
    Task<ArtifactView?> GetReadableAsync(string actor, IReadOnlyList<string> groups, IReadOnlySet<string> roles, Guid artifactId, CancellationToken ct);

    /// <summary>Team-scoped row for the workload PUT plane: the allocating endpoint (same team) may store; foreign ⇒ null.</summary>
    Task<ArtifactView?> GetForWorkloadAsync(LinkPrincipal workload, Guid artifactId, CancellationToken ct);
}

/// <summary>Ledger-backed <see cref="IArtifactLedger"/>.</summary>
public sealed class ArtifactLedger(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    IArtifactStore store,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    ArtifactOptions options) : IArtifactLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AllocationTtl = ArtifactTicket.AllocationTtl;

    /// <inheritdoc />
    public async Task<ArtifactTicket> AllocateAsync(
        string mediaType,
        long size,
        string digest,
        Guid? workId,
        Guid? assignmentId,
        string? retentionPolicy,
        string? comment,
        string? commandId,
        EffectivePrincipal? principal,
        FacilitatorPrincipal? human,
        string origin,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (createdBy, teamSlug, endpointId) = OriginOf(principal, human, origin);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Re-allocation law: the same commandId re-requesting while a pending
        // row lives returns the existing ticket (retry-safe), never a new row.
        if (commandId is not null)
        {
            var existing = await db.Artifacts.AsNoTracking()
                .Where(a => a.CommandId == commandId && a.Origin == origin)
                .FirstOrDefaultAsync(ct);
            if (existing is not null && existing.State == "pending" && existing.ExpiresAt > now)
            {
                return await TicketAsync(existing, ct);
            }
        }

        var row = new ArtifactEntity
        {
            ArtifactId = Guid.NewGuid(),
            WorkId = workId,
            AssignmentId = assignmentId,
            MediaType = mediaType,
            Size = size,
            Digest = digest,
            StorageUri = string.Empty,
            State = "pending",
            Verification = "unverified",
            Origin = origin,
            CreatedBy = createdBy,
            TeamSlug = teamSlug,
            RequestedByEndpointId = endpointId,
            CommandId = commandId,
            CreatedAt = now,
            ExpiresAt = now.Add(AllocationTtl),
            RetentionPolicy = retentionPolicy ?? "permanent",
        };
        db.Artifacts.Add(row);
        audit.Append(db, new AuditRecord(
            Actor: createdBy,
            Action: "artifact.allocated",
            Resource: $"artifact/{row.ArtifactId}",
            Decision: "allow",
            Reason: origin,
            EvidenceRef: null,
            PolicyVersion: "artifact-v1",
            Detail: Serialize(new Dictionary<string, string?>
            {
                ["mediaType"] = mediaType,
                ["size"] = size.ToString(),
                ["digest"] = digest,
                ["workId"] = workId?.ToString(),
                ["comment"] = comment,
            })));
        await db.SaveChangesAsync(ct);
        metrics.ArtifactAllocated();
        return await TicketAsync(row, ct);
    }

    /// <inheritdoc />
    public async Task<ArtifactTicket?> TicketForCommandAsync(string commandId, string origin, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await db.Artifacts.AsNoTracking()
            .Where(a => a.CommandId == commandId && a.Origin == origin && a.State == "pending" && a.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : await TicketAsync(row, ct);
    }

    /// <inheritdoc />
    public async Task<ArtifactStoreResult> StoreAsync(
        Guid artifactId,
        Stream content,
        string workloadOrActorTeam,
        LinkPrincipal? workload,
        CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, ct);
        if (row is null || !TeamMayWrite(row, workloadOrActorTeam, workload))
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotFound);
        }

        if (row.State == "stored")
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.Idempotent, View(row));
        }

        if (row.State != "pending")
        {
            // rejected or deleted — the slot is spent; a fresh allocation is required.
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotFound);
        }

        var putStarted = Stopwatch.GetTimestamp();
        var put = await store.PutAsync(content, ct);
        metrics.ArtifactStoreLatency(Stopwatch.GetElapsedTime(putStarted).TotalMilliseconds);
        var declaredHex = row.Digest["sha256:".Length..];
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (put.Bytes != row.Size)
        {
            await RejectAsync(db, row, "size-mismatch",
                $"declared {row.Size} bytes, received {put.Bytes}", now, ct);
            return new ArtifactStoreResult(ArtifactStoreOutcome.SizeMismatch);
        }

        if (!string.Equals(put.DigestHex, declaredHex, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(db, row, "digest-mismatch",
                $"declared {row.Digest}, computed sha256:{put.DigestHex}", now, ct);
            return new ArtifactStoreResult(ArtifactStoreOutcome.DigestMismatch);
        }

        return await ConfirmStoredAsync(db, artifactId, row, put.Bytes, "digest-verified", now, ct);
    }

    /// <inheritdoc />
    public async Task<ArtifactStoreResult> ConfirmAsync(
        Guid artifactId,
        string workloadOrActorTeam,
        LinkPrincipal? workload,
        CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, ct);
        if (row is null || !TeamMayWrite(row, workloadOrActorTeam, workload))
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotFound);
        }

        if (row.State == "stored")
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.Idempotent, View(row));
        }

        if (row.State != "pending")
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotPending);
        }

        // Only an adapter that mints URLs could have RECEIVED a direct upload,
        // and a row whose scheme a configured adapter doesn't Own is inert
        // (single-source law: the row's scheme, never the live config, decides).
        if (store is not IStoreSignedUrlSource || (row.StorageUri.Length > 0 && !store.Owns(row.StorageUri)))
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotConfirmable);
        }

        var declaredHex = row.Digest["sha256:".Length..];
        var key = ArtifactKeys.RelativePathOf(declaredHex);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var landed = await store.StatAsync(key, ct);
        if (landed is null)
        {
            return new ArtifactStoreResult(ArtifactStoreOutcome.NotFound);
        }

        if (landed.Value != row.Size)
        {
            await RejectAsync(db, row, "size-mismatch",
                $"declared {row.Size} bytes, object holds {landed.Value}", now, ct);
            return new ArtifactStoreResult(ArtifactStoreOutcome.SizeMismatch);
        }

        // D5: kernel re-hash, never ETag trust — same audit/metrics law as the
        // proxied PUT, so a direct upload cannot smuggle a squatting blob.
        if (!await store.VerifyDigestAsync(key, declaredHex, ct))
        {
            await RejectAsync(db, row, "digest-mismatch",
                $"declared {row.Digest}, object at {key} hashes elsewhere", now, ct);
            return new ArtifactStoreResult(ArtifactStoreOutcome.DigestMismatch);
        }

        var confirmed = await ConfirmStoredAsync(db, artifactId, row, row.Size, "direct-upload-verified", now, ct);
        if (confirmed.Outcome == ArtifactStoreOutcome.Stored)
        {
            metrics.ArtifactConfirmed(workload is null ? "rest" : "link");
        }

        return confirmed;
    }

    /// <summary>The DB-arbitrated pending→stored law shared by the proxied PUT and the confirm path: one conditional update, then audit + timeline + metrics in the same transaction.</summary>
    private async Task<ArtifactStoreResult> ConfirmStoredAsync(
        FacilitatorDbContext db, Guid artifactId, ArtifactEntity row, long bytes, string reason, DateTime now, CancellationToken ct)
    {
        var storageUri = store.StorageUriScheme + ":" + ArtifactKeys.RelativePathOf(row.Digest["sha256:".Length..]);
        var rows = await db.Artifacts
            .Where(a => a.ArtifactId == artifactId && a.State == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, "stored")
                .SetProperty(a => a.Verification, "verified")
                .SetProperty(a => a.StorageUri, storageUri)
                .SetProperty(a => a.StoredAt, now), ct);
        if (rows == 0)
        {
            var raced = await db.Artifacts.AsNoTracking().FirstAsync(a => a.ArtifactId == artifactId, ct);
            return new ArtifactStoreResult(
                raced.State == "stored" ? ArtifactStoreOutcome.Idempotent : ArtifactStoreOutcome.NotFound,
                View(raced));
        }

        db.Artifacts.Local.Clear();
        var after = await db.Artifacts.AsNoTracking().FirstAsync(a => a.ArtifactId == artifactId, ct);
        audit.Append(db, new AuditRecord(
            Actor: after.CreatedBy,
            Action: "artifact.stored",
            Resource: $"artifact/{artifactId}",
            Decision: "allow",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "artifact-v1",
            Detail: Serialize(new Dictionary<string, string?> { ["digest"] = after.Digest, ["bytes"] = bytes.ToString() })));
        await AppendTimelineAsync(db, after, "artifact.stored", after.CreatedAt, now, ct);
        await db.SaveChangesAsync(ct);
        metrics.ArtifactStored();
        return new ArtifactStoreResult(ArtifactStoreOutcome.Stored, View(after));
    }

    /// <inheritdoc />
    public async Task<ArtifactRegisterResult> RegisterExternalAsync(JsonElement eventRoot, CancellationToken ct)
    {
        var data = eventRoot.GetProperty("data");
        var wireId = data.GetProperty("artifactId").GetString() ?? string.Empty;
        var mediaType = data.GetProperty("mediaType").GetString() ?? "application/octet-stream";
        var size = data.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szi) ? szi : 0;
        var digest = data.GetProperty("digest").GetString() ?? string.Empty;
        var storageUrl = data.TryGetProperty("storageUrl", out var su) ? su.GetString() : null;
        var createdBy = data.TryGetProperty("createdBy", out var cb) ? cb.GetString() : null;
        Guid? workId = data.TryGetProperty("workId", out var w) && Guid.TryParse(w.GetString(), out var wid) ? wid : null;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Kernel-allocated ids confirm; anything else registers as external.
        if (Guid.TryParse(wireId, out var kernelId))
        {
            var row = await db.Artifacts.FirstOrDefaultAsync(a => a.ArtifactId == kernelId, ct);
            if (row is not null)
            {
                if (!string.Equals(row.MediaType, mediaType, StringComparison.Ordinal)
                    || row.Size != size
                    || !string.Equals(row.Digest, digest, StringComparison.OrdinalIgnoreCase))
                {
                    audit.Append(db, new AuditRecord(
                        Actor: createdBy ?? "external",
                        Action: "artifact.reregistration-denied",
                        Resource: $"artifact/{kernelId}",
                        Decision: "deny",
                        Reason: "declaration-mismatch",
                        EvidenceRef: null,
                        PolicyVersion: "artifact-v1",
                        Detail: Serialize(new Dictionary<string, string?>
                        {
                            ["declaredDigest"] = digest,
                            ["recordedDigest"] = row.Digest,
                        })));
                    await db.SaveChangesAsync(ct);
                    return new ArtifactRegisterResult(ArtifactRegisterOutcome.DeclarationMismatch, kernelId);
                }

                if (row.State == "pending")
                {
                    // Confirmed before the bytes landed (event-first): mark
                    // pending→stored with the event's URL. Verification is the
                    // screen's verdict — a binding or a clean SSRF screen
                    // upgrades, anything else (including no URL at all) stays
                    // unverified; the kernel never fetched the bytes.
                    row.State = "stored";
                    row.StorageUri = $"external:{storageUrl ?? wireId}";
                    var (confirmedAllowed, confirmedReason) = storageUrl is null
                        ? (false, "no-url")
                        : await ExternalRefScreener.ScreenAsync(storageUrl, options.ExternalBindings);
                    row.Verification = confirmedAllowed ? "verified" : "unverified";
                    audit.Append(db, new AuditRecord(
                        Actor: createdBy ?? "external",
                        Action: "artifact.external-screened",
                        Resource: $"artifact/{kernelId}",
                        Decision: "allow",
                        Reason: confirmedReason,
                        EvidenceRef: storageUrl,
                        PolicyVersion: "artifact-v1",
                        Detail: Serialize(new Dictionary<string, string?>
                        {
                            ["stage"] = "confirm",
                            ["screen"] = confirmedReason,
                            ["verification"] = row.Verification,
                        })));
                    row.StoredAt = now;
                }

                await AppendTimelineAsync(db, row, "artifact.available", row.CreatedAt, now, ct);
                await db.SaveChangesAsync(ct);
                return new ArtifactRegisterResult(ArtifactRegisterOutcome.Confirmed, kernelId);
            }
        }

        // Redelivery law: an earlier external row with the same wire label +
        // identical digest is the same declaration — no second row.
        var prior = await db.Artifacts.AsNoTracking()
            .Where(a => a.WireArtifactId == wireId && a.Origin == "external" && a.Digest == digest)
            .FirstOrDefaultAsync(ct);
        if (prior is not null)
        {
            return new ArtifactRegisterResult(ArtifactRegisterOutcome.Duplicate, prior.ArtifactId);
        }

        // Screen the declared URL: a binding prefix or a clean SSRF screen
        // makes the claim verified; anything else stays unverified (never a
        // refusal — a bad screen downgrades, it doesn't reject).
        var (screenAllowed, screenReason) = storageUrl is null
            ? (false, "no-url")
            : await ExternalRefScreener.ScreenAsync(storageUrl, options.ExternalBindings);
        var external = new ArtifactEntity
        {
            ArtifactId = Guid.NewGuid(),
            WorkId = workId,
            MediaType = mediaType,
            Size = size,
            Digest = digest,
            StorageUri = $"external:{storageUrl ?? wireId}",
            State = "stored",
            Verification = screenAllowed ? "verified" : "unverified",
            Origin = "external",
            CreatedBy = createdBy ?? "external",
            TeamSlug = null,
            WireArtifactId = wireId,
            CreatedAt = now,
            ExpiresAt = now,
            StoredAt = now,
        };
        db.Artifacts.Add(external);
        audit.Append(db, new AuditRecord(
            Actor: external.CreatedBy,
            Action: "artifact.registered",
            Resource: $"artifact/{external.ArtifactId}",
            Decision: "allow",
            Reason: "external",
            EvidenceRef: storageUrl,
            PolicyVersion: "artifact-v1",
            Detail: Serialize(new Dictionary<string, string?>
            {
                ["wireArtifactId"] = wireId,
                ["digest"] = digest,
                ["screen"] = screenReason,
                ["verification"] = external.Verification,
            })));
        if (external.WorkId is not null)
        {
            await AppendTimelineAsync(db, external, "artifact.registered", external.CreatedAt, now, ct);
        }

        await db.SaveChangesAsync(ct);
        metrics.ArtifactRegistered();
        return new ArtifactRegisterResult(ArtifactRegisterOutcome.Registered, external.ArtifactId);
    }

    /// <inheritdoc />
    public async Task<ArtifactView?> GetAsync(
        string actor, IReadOnlyList<string> groups, IReadOnlySet<string> roles, Guid artifactId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, ct);
        return row is not null && TeamMayRead(row, groups, roles) ? View(row) : null;
    }

    /// <inheritdoc />
    public async Task<ArtifactView?> GetReadableAsync(
        string actor, IReadOnlyList<string> groups, IReadOnlySet<string> roles, Guid artifactId, CancellationToken ct)
    {
        var view = await GetAsync(actor, groups, roles, artifactId, ct);
        // Kernel-hosted only: a configured adapter Owns() the row's scheme (external:
        // rows and foreign-scheme rows answer unreadable — the scheme on the row is
        // the single source of truth, never a re-read of Storage:Provider).
        return view is { State: "stored" } && store.Owns(view.StorageUri) ? view : null;
    }

    /// <inheritdoc />
    public async Task<ArtifactView?> GetForWorkloadAsync(LinkPrincipal workload, Guid artifactId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, ct);
        return row is not null && string.Equals(row.TeamSlug, workload.TeamSlug, StringComparison.Ordinal) ? View(row) : null;
    }

    private static (string CreatedBy, string? TeamSlug, string? EndpointId) OriginOf(
        EffectivePrincipal? principal, FacilitatorPrincipal? human, string origin)
    {
        if (origin == "link" && principal is not null)
        {
            // Effective authority: the delegated human when validated, else the workload endpoint.
            var createdBy = principal.Delegated?.Subject ?? $"link:{principal.Workload.EndpointId}";
            return (createdBy, principal.Workload.TeamSlug, principal.Workload.EndpointId);
        }

        if (human is not null)
        {
            return (human.Actor, TeamOfGroups(human.Groups), null);
        }

        throw new InvalidOperationException("artifact allocation requires a principal for its origin");
    }

    /// <summary>The first <c>/teams/&lt;slug&gt;</c> group is the actor's team scope.</summary>
    public static string? TeamOfGroups(IReadOnlyList<string> groups)
    {
        foreach (var group in groups)
        {
            if (group.StartsWith("/teams/", StringComparison.Ordinal) && group.Length > "/teams/".Length)
            {
                return group["/teams/".Length..];
            }
        }

        return null;
    }

    private static bool TeamMayRead(ArtifactEntity row, IReadOnlyList<string> groups, IReadOnlySet<string> roles) =>
        roles.Contains("framework-admin")
        || (row.TeamSlug is not null && groups.Contains($"/teams/{row.TeamSlug}"));

    private static bool TeamMayWrite(ArtifactEntity row, string workloadOrActorTeam, LinkPrincipal? workload) =>
        workload is null || string.Equals(row.TeamSlug, workloadOrActorTeam, StringComparison.Ordinal);

    private async Task RejectAsync(
        FacilitatorDbContext db, ArtifactEntity row, string reason, string detail, DateTime now, CancellationToken ct)
    {
        await db.Artifacts
            .Where(a => a.ArtifactId == row.ArtifactId && a.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "rejected"), ct);
        audit.Append(db, new AuditRecord(
            Actor: row.CreatedBy,
            Action: "artifact.rejected",
            Resource: $"artifact/{row.ArtifactId}",
            Decision: "deny",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "artifact-v1",
            Detail: Serialize(new Dictionary<string, string?> { ["reason"] = reason, ["detail"] = detail })));
        await db.SaveChangesAsync(ct);
        metrics.ArtifactRejected(reason.Contains("size", StringComparison.Ordinal) ? "size" : "digest");
    }

    private static async Task AppendTimelineAsync(
        FacilitatorDbContext db, ArtifactEntity row, string kind, DateTime createdAt, DateTime now, CancellationToken ct)
    {
        if (row.WorkId is not { } workId)
        {
            return;
        }

        var version = await db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = kind,
            Actor = row.CreatedBy,
            Detail = Serialize(new Dictionary<string, string?>
            {
                ["artifactId"] = row.ArtifactId.ToString(),
                ["digest"] = row.Digest,
                ["mediaType"] = row.MediaType,
            }),
            CorrelationId = row.ArtifactId.ToString(),
            CreatedAt = now,
        });
    }

    // A pending slot's ticket: when the configured adapter mints URLs, the
    // ticket carries a presigned PUT for the digest-derived key the admission
    // already fixed (D2) — the same key the bytes MUST land at, so a
    // client-chosen address can never squat another digest's location. The URL
    // expires WITH the allocation (one timestamp). A row that already got
    // re-pointed (event-first confirmation wrote external:) gets no URL.
    private async Task<ArtifactTicket> TicketAsync(ArtifactEntity row, CancellationToken ct)
    {
        string? uploadUrl = null;
        if (row.State == "pending" && row.StorageUri.Length == 0
            && store is IStoreSignedUrlSource source
            && await source.PresignUploadAsync(
                ArtifactKeys.RelativePathOf(row.Digest["sha256:".Length..]), AllocationTtl, ct) is { } url)
        {
            uploadUrl = url.ToString();
            metrics.ArtifactPresigned("upload");
        }

        return new ArtifactTicket(row.ArtifactId, UploadLocation(row), uploadUrl,
            uploadUrl is null ? "POST" : "PUT", row.ExpiresAt);
    }

    private static string UploadLocation(ArtifactEntity row) =>
        row.Origin == "link"
            ? $"/api/link/v1/artifacts/{row.ArtifactId}/content"
            : $"/api/artifacts/{row.ArtifactId}/content";

    private static ArtifactView View(ArtifactEntity row) =>
        new(row.ArtifactId, row.WorkId, row.AssignmentId, row.MediaType, row.Size, row.Digest,
            row.State, row.Verification, row.Origin, row.CreatedBy, row.TeamSlug, row.CreatedAt,
            row.StoredAt, row.RetentionPolicy)
        {
            StorageUri = row.StorageUri,
        };

    private static string Serialize(Dictionary<string, string?> detail) =>
        JsonSerializer.Serialize(detail, Json);
}
