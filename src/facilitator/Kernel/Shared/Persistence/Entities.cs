namespace Facilitator.Kernel.Shared.Persistence;

// Table-backed entity classes (schema `facilitator`, see FacilitatorDbContext
// for the ADR header). Guid ids are generated client-side; all timestamps are
// DateTime in UTC (Npgsql legacy timestamp behaviour is NOT enabled).

/// <summary>facilitator.teams — org unit; the roster seeder lands rows at startup.</summary>
public sealed class TeamEntity
{
    public Guid TeamId { get; set; }

    public required string Slug { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>facilitator.team_members — the team roster: who belongs to a team
/// and in what capacity. Config seeds it at startup when a row is absent (DB is
/// truth after the first write); the roster API mutates it thereafter.</summary>
public sealed class TeamMemberEntity
{
    public Guid TeamId { get; set; }

    /// <summary>Canonical actor: <c>oidc:&lt;iss&gt;|&lt;sub&gt;</c> or <c>contributor:&lt;handle&gt;</c>.</summary>
    public required string Actor { get; set; }

    /// <summary>human | contributor (CHECK-constrained).</summary>
    public required string Kind { get; set; }

    /// <summary>member | operator (CHECK-constrained); operator grants the built-in team-operator role.</summary>
    public required string Capacity { get; set; }

    /// <summary>Canonical actor of whoever added the row ("config" when seeded).</summary>
    public string? AddedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public TeamEntity? Team { get; set; }
}

/// <summary>facilitator.team_settings — per-scope switch rows (team or channel
/// scope) for one resource; the team/channel layer of effective config.</summary>
public sealed class TeamSettingEntity
{
    public Guid TeamId { get; set; }

    /// <summary>team | channel (CHECK-constrained).</summary>
    public required string ScopeKind { get; set; }

    /// <summary>Team slug for <c>team</c>; <c>&lt;provider&gt;/&lt;channel&gt;</c> for <c>channel</c>.</summary>
    public required string ScopeId { get; set; }

    /// <summary>Resource name (the admin-config registry namespace).</summary>
    public required string Resource { get; set; }

    /// <summary>jsonb document.</summary>
    public required string Payload { get; set; }

    /// <summary>Optimistic fencing version (the If-Match ETag).</summary>
    public long RowVersion { get; set; }

    /// <summary>Canonical actor of the last writer.</summary>
    public required string Author { get; set; }

    /// <summary>Change reason of the last write.</summary>
    public required string Reason { get; set; }

    public DateTime UpdatedAt { get; set; }

    public TeamEntity? Team { get; set; }
}

/// <summary>facilitator.projects — work container within a team.</summary>
public sealed class ProjectEntity
{
    public Guid ProjectId { get; set; }

    public Guid TeamId { get; set; }

    public required string Name { get; set; }

    /// <summary>jsonb document.</summary>
    public required string Config { get; set; }

    public DateTime CreatedAt { get; set; }

    public TeamEntity? Team { get; set; }
}

/// <summary>facilitator.works — the Work aggregate root.</summary>
public sealed class WorkEntity
{
    public Guid WorkId { get; set; }

    public Guid? ProjectId { get; set; }

    public required string Objective { get; set; }

    /// <summary>Wire state name (CHECK-constrained to the set).</summary>
    public required string State { get; set; }

    /// <summary>Optimistic-fencing version; bumped atomically with every state change.</summary>
    public long Version { get; set; }

    /// <summary>Inbox row that created this Work (lineage).</summary>
    public Guid? SourceEventId { get; set; }

    /// <summary>Owning team (set directly for new rows; backfilled from the project for old ones).</summary>
    public Guid? TeamId { get; set; }

    /// <summary>Canonical accountable actor: <c>oidc:iss|sub</c> or <c>contributor:handle</c>; null = team attention.</summary>
    public string? OwnerActor { get; set; }

    /// <summary>The first wire type that delivered this work (origin disclosure, never inferred).</summary>
    public string? Origin { get; set; }

    /// <summary>jsonb <c>{provider,channel,root,kind}</c>: where the work currently lives.</summary>
    public string? ActiveWhere { get; set; }

    /// <summary>jsonb <c>{tokens,units,currency,source,at}</c>: cost as reported by the executor.</summary>
    public string? Cost { get; set; }

    /// <summary>Set by a merge: this work's history stays, reads redirect to the survivor.</summary>
    public Guid? MergedInto { get; set; }

    /// <summary>Why a work closed without artifacts/manifest/external links (closure evidence).</summary>
    public string? CloseReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ProjectEntity? Project { get; set; }

    public TeamEntity? Team { get; set; }
}

/// <summary>facilitator.work_links — typed relations between works (successors,
/// merges, hierarchy, shared context). A link is the durable statement that two
/// works relate; the history of each side stays intact.</summary>
public sealed class WorkLinkEntity
{
    public Guid LinkId { get; set; }

    public Guid WorkId { get; set; }

    public Guid RefWorkId { get; set; }

    /// <summary>successor | parent | child | merged-duplicate | context (CHECK-constrained).</summary>
    public required string Kind { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }

    public WorkEntity? Work { get; set; }
}

/// <summary>facilitator.assignments — Assignment aggregate.</summary>
public sealed class AssignmentEntity
{
    public Guid AssignmentId { get; set; }

    public Guid WorkId { get; set; }

    public Guid? TeamId { get; set; }

    public string? CapabilityId { get; set; }

    public required string Status { get; set; }

    /// <summary>jsonb — sibling assignment UUIDs gating this intent (the DAG gate).</summary>
    public string? DependsOn { get; set; }

    public int CurrentExecutionGeneration { get; set; }

    public long? ExpectedContextVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public WorkEntity? Work { get; set; }
}

/// <summary>facilitator.execution_attempts — one attempt per generation.</summary>
public sealed class ExecutionAttemptEntity
{
    public Guid ExecutionId { get; set; }

    public Guid AssignmentId { get; set; }

    public int Generation { get; set; }

    public required string Status { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public AssignmentEntity? Assignment { get; set; }
}

/// <summary>facilitator.nudges — bounded-nudge ledger, one row per stall episode.</summary>
public sealed class NudgeLedgerEntity
{
    public Guid AssignmentId { get; set; }

    public int Generation { get; set; }

    public int StallEpisode { get; set; }

    public int NudgeCount { get; set; }

    public DateTime LastNudgeAt { get; set; }

    public DateTime LastProgressAt { get; set; }

    public AssignmentEntity? Assignment { get; set; }
}

/// <summary>facilitator.work_events — append-only timeline + audit.</summary>
public sealed class WorkEventEntity
{
    public Guid EventId { get; set; }

    public Guid WorkId { get; set; }

    /// <summary>Work version AFTER this event was applied.</summary>
    public long WorkVersion { get; set; }

    /// <summary>created | state.transition | steer | command.accepted | assignment.transition</summary>
    public required string Kind { get; set; }

    public required string Actor { get; set; }

    /// <summary>jsonb document.</summary>
    public required string Detail { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime CreatedAt { get; set; }

    public WorkEntity? Work { get; set; }
}

/// <summary>facilitator.inbox — transactional ingress (dedupe inbox).</summary>
public sealed class InboxEntity
{
    public Guid InboxId { get; set; }

    public required string EventSource { get; set; }

    public required string EventId { get; set; }

    /// <summary>Business-command dedupe key (unique when present).</summary>
    public string? CommandId { get; set; }

    public string? Provider { get; set; }

    public string? ProviderDeliveryId { get; set; }

    /// <summary>jsonb document (the full source envelope/body).</summary>
    public required string Payload { get; set; }

    /// <summary>sha256 hex of the exact wire bytes (provider-path audit).</summary>
    public required string PayloadSha256 { get; set; }

    public Guid? WorkId { get; set; }

    /// <summary>pending | processing | processed | failed | duplicate</summary>
    public string State { get; set; } = "pending";

    public DateTime AvailableAt { get; set; }

    public DateTime? LeasedUntil { get; set; }

    public string? LeaseOwner { get; set; }

    public int Attempt { get; set; }

    public string? Error { get; set; }

    public DateTime ReceivedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }
}

/// <summary>facilitator.outbox — transactional egress.</summary>
public sealed class OutboxEntity
{
    public Guid OutboxId { get; set; }

    /// <summary>Logical channel address, e.g. <c>kernel.log</c> or <c>link.&lt;endpointId&gt;</c>.</summary>
    public required string RecipientEndpointId { get; set; }

    /// <summary>work | assignment</summary>
    public required string AggregateType { get; set; }

    public Guid? AggregateId { get; set; }

    public required string EventType { get; set; }

    /// <summary>Business idempotency key (unique).</summary>
    public required string CommandId { get; set; }

    /// <summary>jsonb document — the full CloudEvent envelope.</summary>
    public required string Envelope { get; set; }

    public Guid? WorkId { get; set; }

    /// <summary>pending | leased | delivered | dead_letter</summary>
    public string Status { get; set; } = "pending";

    public DateTime AvailableAt { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTime? LeasedUntil { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }
}

/// <summary>facilitator.dead_letters: diagnostics + original CloudEvent.</summary>
public sealed class DeadLetterEntity
{
    public Guid DeadLetterId { get; set; }

    public Guid OutboxId { get; set; }

    public required string Reason { get; set; }

    /// <summary>jsonb document — original CloudEvent snapshot.</summary>
    public required string Envelope { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>facilitator.provider_events — audit of the full provider source.</summary>
public sealed class ProviderEventEntity
{
    public Guid ProviderEventId { get; set; }

    public required string Provider { get; set; }

    public string? ProviderDeliveryId { get; set; }

    public required string BodySha256 { get; set; }

    /// <summary>jsonb document.</summary>
    public required string Body { get; set; }

    public string? VerifiedReference { get; set; }

    public DateTime ReceivedAt { get; set; }
}

/// <summary>facilitator.link_sessions — session leases.</summary>
public sealed class LinkSessionEntity
{
    public required string LogicalEndpointId { get; set; }

    public required string ConnectionInstanceId { get; set; }

    public required string ReplicaId { get; set; }

    public long SessionEpoch { get; set; }

    public DateTime LeaseUntil { get; set; }

    public DateTime LastHeartbeat { get; set; }

    public string? CapabilitiesDigest { get; set; }

    /// <summary>When this endpoint's session was FIRST opened (preserved
    /// across re-opens; the row is deleted on close so a fresh enrollment
    /// resets it). The routing funnel's "oldest online" tie-break reads it.</summary>
    public DateTime OpenedAt { get; set; }
}

/// <summary>facilitator.routing_decisions — one audit row per routing intent:
/// the kernel-computed allowed set, the chosen endpoint, the basis, and (for
/// an undecided pass) the requeue reason the scale-up trigger reads.</summary>
public sealed class RoutingDecisionEntity
{
    public Guid DecisionId { get; set; }

    public required Guid WorkId { get; set; }

    public required string IntentCommandId { get; set; }

    public required string CapabilityId { get; set; }

    public string? TeamSlug { get; set; }

    /// <summary>jsonb ordered array of allowed endpoint ids.</summary>
    public required string AllowedCandidates { get; set; }

    public string? ChosenEndpointId { get; set; }

    public required string Status { get; set; }

    public required string Basis { get; set; }

    public string? ProviderRef { get; set; }

    public DateTime LastRoutingAttemptAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Why the ask waits for a human (e.g. "triage"): the work sits blocked until one picks it up.</summary>
    public string? RoutingTriage { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.link_enrollments — enrollment records.</summary>
public sealed class LinkEnrollmentEntity
{
    public Guid EnrollmentId { get; set; }

    public required string EndpointId { get; set; }

    public Guid TeamId { get; set; }

    public required string TokenSha256 { get; set; }

    /// <summary>shared-runtime | separate-process-same-host | isolated-executor</summary>
    public required string TrustMode { get; set; }

    /// <summary>jsonb CapabilityManifestV1 or NULL.</summary>
    public string? CapabilityManifest { get; set; }

    public string? CapabilitiesDigest { get; set; }

    public DateTime EnrolledAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>gateway | child — role bound at enrollment; rotation never re-roles.</summary>
    public required string Role { get; set; }

    /// <summary>True when this enrollment accepts oidc: delegated evidence claims (mode (b) bindings only).</summary>
    public bool AcceptsDelegatedClaims { get; set; }

    /// <summary>Mode (b): issuer of the workload token that enrolled this endpoint (null for mode (a)).</summary>
    public string? WorkloadIssuer { get; set; }

    /// <summary>Mode (b): subject (client id / service account) of the workload token.</summary>
    public string? WorkloadSubject { get; set; }
}

/// <summary>facilitator.enrollment_tokens — mintable, revocable enrollment tokens (admin-minted; consumed on first enroll).</summary>
public sealed class EnrollmentTokenEntity
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (base64url) of the minted token literal — the literal itself is never stored.</summary>
    public required string TokenHash { get; set; }

    public required string Label { get; set; }

    /// <summary>Team roster slug the token binds.</summary>
    public required string Team { get; set; }

    /// <summary>gateway | child — role the consuming enrollment must carry.</summary>
    public required string Role { get; set; }

    /// <summary>Subject claim of the minting principal (audit).</summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>First use (the enroll that consumed it); afterwards the token is that endpoint's standing re-enroll secret.</summary>
    public DateTime? ConsumedAt { get; set; }

    public string? ConsumedEndpointId { get; set; }

    /// <summary>Enrollment minted on consumption — revoke kills this link enrollment too.</summary>
    public Guid? LinkEnrollmentId { get; set; }
}

/// <summary>facilitator.link_deliveries — durable delivery/receipt ledger.</summary>
public sealed class LinkDeliveryEntity
{
    public Guid DeliveryId { get; set; }

    public required string EndpointId { get; set; }

    public long SessionEpoch { get; set; }

    /// <summary>pending | in_flight | acked</summary>
    public required string State { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public DateTime? AckedAt { get; set; }

    /// <summary>jsonb — last receipt verbatim.</summary>
    public string? Receipt { get; set; }

    public int Attempts { get; set; }

    public DateTime? RedeliverAfter { get; set; }
}

/// <summary>facilitator.approvals — approval lifecycle authority (record/respond/consume/revoke).</summary>
public sealed class ApprovalEntity
{
    public Guid ApprovalId { get; set; }

    public Guid? WorkId { get; set; }

    public required string ActionDigest { get; set; }

    /// <summary>pending | approved | denied | expired | revoked | consumed</summary>
    public string State { get; set; } = "pending";

    public required string Nonce { get; set; }

    /// <summary>jsonb document.</summary>
    public required string EligibleActors { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    /// <summary>Canonical actor of the deciding principal (oidc:iss|sub).</summary>
    public string? DecidedBy { get; set; }

    /// <summary>The responder's actorEvidenceRef, verbatim.</summary>
    public string? ActorEvidenceRef { get; set; }

    /// <summary>Responder comment (<= 1024).</summary>
    public string? Comment { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>Digest provenance: recomputed | digest-unverified (1.0 senders without action).</summary>
    public string DigestVerification { get; set; } = "recomputed";

    /// <summary>The Link endpoint (Gateway) that recorded this approval;
    /// decided-push addressing + consume authorization. Null for non-Link recorders.</summary>
    public string? RequestedByEndpointId { get; set; }
}

/// <summary>facilitator.communication_bindings — canonical conversation →
/// Work binding (thread routing + reply addressing + outbound post ledger).</summary>
public sealed class CommunicationBindingEntity
{
    public Guid BindingId { get; set; }

    public required string Provider { get; set; }

    public required string Channel { get; set; }

    public required string RootMessageId { get; set; }

    public string? ParentMessageId { get; set; }

    public Guid? WorkId { get; set; }

    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>facilitator.provider_identities — verified provider-account mapping
/// (provider, tenant, accountId) → (oidc issuer, subject) with redemption provenance.</summary>
public sealed class ProviderIdentityEntity
{
    public Guid IdentityId { get; set; }

    public required string Provider { get; set; }

    public required string Tenant { get; set; }

    public required string AccountId { get; set; }

    public required string OidcIssuer { get; set; }

    public required string OidcSubject { get; set; }

    /// <summary>The challenge row that provened this mapping (single-use code).</summary>
    public Guid? ChallengeId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.provider_link_challenges — single-use linking codes:
/// minted by a /link mention, delivered by bot DM, redeemed on the human plane.</summary>
public sealed class ProviderLinkChallengeEntity
{
    public Guid ChallengeId { get; set; }

    /// <summary>sha256 hex of the code (raw code never stored).</summary>
    public required string CodeSha256 { get; set; }

    public required string Provider { get; set; }

    public required string Tenant { get; set; }

    public required string AccountId { get; set; }

    /// <summary>Bot-DM delivery target (provider account id of the enroller).</summary>
    public required string DeliverToAccountId { get; set; }

    /// <summary>Human identity captured at redemption (D2: redemption is a human-plane call).</summary>
    public string? OidcIssuer { get; set; }

    public string? OidcSubject { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RedeemedAt { get; set; }
}

/// <summary>facilitator.audit — every authorization-gate decision, allow and deny.</summary>
public sealed class AuditEventEntity
{
    public Guid AuditId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>Normalized actor ("oidc:iss|sub"); null when the caller was unauthenticated (never for gate rows).</summary>
    public string? Actor { get; set; }

    /// <summary>Decision subject, e.g. "authorization.evaluated:work.write" or "identity.role.upsert".</summary>
    public required string Action { get; set; }

    /// <summary>Request path/route the decision covered.</summary>
    public string? Resource { get; set; }

    /// <summary>allow | deny</summary>
    public required string Decision { get; set; }

    /// <summary>Machine-readable reason (e.g. no-role-match, no-binding, audit-unavailable).</summary>
    public string? Reason { get; set; }

    /// <summary>Pointer to corroborating evidence (approval id, ledger row).</summary>
    public string? EvidenceRef { get; set; }

    /// <summary>Role-rule policy version at decision time (row-version epoch).</summary>
    public string? PolicyVersion { get; set; }

    /// <summary>jsonb decision context — never a raw token.</summary>
    public required string Detail { get; set; }

    public string? TraceId { get; set; }

    public string? CorrelationId { get; set; }
}

/// <summary>facilitator.identity_roles — runtime-mutable role rule sets (config seeds once, DB is truth).</summary>
public sealed class IdentityRoleEntity
{
    /// <summary>Role name (PK).</summary>
    public required string RoleName { get; set; }

    /// <summary>jsonb array of {all:[{claim,op,value}]} rules.</summary>
    public required string Rules { get; set; }

    /// <summary>Optimistic-concurrency counter (PUT mismatch -> 409).</summary>
    public long RowVersion { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.evidence — kernel-issued evidence references (admission validation, ledger mint/consume).</summary>
public sealed class EvidenceEntity
{
    /// <summary>The id after the <c>kernel:</c> scheme (PK).</summary>
    public required string EvidenceId { get; set; }

    /// <summary>Canonical subject the evidence was minted for.</summary>
    public required string Subject { get; set; }

    /// <summary>jsonb claim snapshot taken at mint time.</summary>
    public required string Claims { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set once consumed (replay law: a second use fails validation).</summary>
    public DateTime? ConsumedAt { get; set; }
}

/// <summary>facilitator.artifacts — durable artifact metadata ledger (the kernel is the artifact
/// authority: allocation, digest-verified store, external registration, retention hook).</summary>
public sealed class ArtifactEntity
{
    public Guid ArtifactId { get; set; }

    public Guid? WorkId { get; set; }

    public Guid? AssignmentId { get; set; }

    /// <summary>Declared media type — the kernel never parses it.</summary>
    public required string MediaType { get; set; }

    /// <summary>Declared byte size; the store path counts actual bytes and rejects a lie.</summary>
    public long Size { get; set; }

    /// <summary>Declared digest, <c>sha256:[0-9a-f]{64}</c>; verified at ingest when stored here.</summary>
    public required string Digest { get; set; }

    /// <summary><c>kernel-fs:&lt;relpath&gt;</c> (verified bytes here) or <c>external:&lt;url&gt;</c> (unverified).</summary>
    public required string StorageUri { get; set; }

    /// <summary>pending | stored | rejected | deleted</summary>
    public string State { get; set; } = "pending";

    /// <summary>verified | unverified — verified only when the kernel hashed the bytes itself.</summary>
    public string Verification { get; set; } = "unverified";

    /// <summary>link | rest | external — the plane that allocated/registered the row.</summary>
    public required string Origin { get; set; }

    /// <summary>Canonical actor of the allocating principal (oidc:iss|sub or link:endpoint).</summary>
    public required string CreatedBy { get; set; }

    /// <summary>Team scope of the row (non-disclosure boundary); null for team-less external registrations.</summary>
    public string? TeamSlug { get; set; }

    /// <summary>The Link endpoint that allocated the slot (ownership audit); null on the REST plane.</summary>
    public string? RequestedByEndpointId { get; set; }

    /// <summary>Wire commandId of the allocating request_upload (duplicate acks re-attach its ticket).</summary>
    public string? CommandId { get; set; }

    /// <summary>For non-Guid wire artifactIds (external path): the declared label.</summary>
    public string? WireArtifactId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Upload-slot expiry for pending rows (re-allocation is idempotent until expiry).</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? StoredAt { get; set; }

    /// <summary>permanent | (future GC policies) — enforcement is the storage plan's.</summary>
    public string RetentionPolicy { get; set; } = "permanent";
}

/// <summary>
/// facilitator.result_manifests — durable ledger of result manifests recorded against a work
/// — the kernel is the authority on the manifest revision chain, the declared digests,
/// and the GC pin. The manifest document itself lives as content-addressed bytes in the artifact
/// store; this row records its identity, its revision lineage, and the output artifact ids it
/// declares (flattened so the retention sweep can pin referenced blobs in a single set-based check).
/// </summary>
public sealed class ResultManifestEntity
{
    /// <summary>The manifest id as declared by the stored document and by the completed event's inline <c>resultManifest</c> (PK).</summary>
    public string ManifestId { get; set; } = string.Empty;
    public Guid WorkId { get; set; }
    public Guid AssignmentId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    /// <summary>1-based revision chain head per assignment; corrections supersede the previous head in the same tx.</summary>
    public int Revision { get; set; }
    /// <summary>The manifest id this revision supersedes; null on the first revision.</summary>
    public string? RevisionOf { get; set; }
    /// <summary>Wire digest ("sha256:&lt;64hex&gt;") of the canonical manifest document, verified against the stored bytes.</summary>
    public string Digest { get; set; } = string.Empty;
    /// <summary>Digest of the canonical <c>structuredResult</c> sub-document when present, else null.</summary>
    public string? StructuredDigest { get; set; }
    /// <summary>The verified artifact row carrying the manifest bytes.</summary>
    public Guid ArtifactId { get; set; }
    /// <summary>Every <c>outputs[].artifactId</c> the document declares (lower-case GUID strings); empty array when none.</summary>
    public string[] OutputArtifactIds { get; set; } = [];
    /// <summary>missing | available | validated | rejected | superseded.</summary>
    public string ResultState { get; set; } = "available";
    /// <summary>recorded | superseded — the chain-law state of this row.</summary>
    public string State { get; set; } = "recorded";
    public DateTime RecordedAt { get; set; }
}

/// <summary>
/// facilitator.memory_scopes — one home scope per memory entry:
/// rows are provisioned lazily by retain (D6), owned by exactly one team. The
/// canonical selector is the broker-resolved identity, never a worker claim.
/// </summary>
public sealed class MemoryScopeEntity
{
    public Guid ScopeId { get; set; }
    /// <summary>work | project | team | private (CHECK).</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Owning team — the ACL anchor every readable-scope predicate starts from.</summary>
    public string TeamSlug { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public Guid? WorkId { get; set; }
    /// <summary>Broker-resolved selector (e.g. <c>project:&lt;guid&gt;</c>); unique.</summary>
    public string CanonicalSelector { get; set; } = string.Empty;
    /// <summary>Retention override materialized from per-Project policy at provisioning (D7); null = default.</summary>
    public int? RetentionDays { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// facilitator.memory_entries — the broker's searchable knowledge rows.
/// <c>team_slug</c> is denormalized from the scope for the single-table ACL
/// predicate (D2: ACL before search, always in SQL). Tombstoning empties
/// <c>content</c> in the same tx (D5), which recomputes the stored tsvector
/// generated column — the broker never reads or writes that column.
/// </summary>
public sealed class MemoryEntryEntity
{
    public Guid EntryId { get; set; }
    public Guid ScopeId { get; set; }
    /// <summary>Owner team (denormalized from the scope for the ACL predicate).</summary>
    public string TeamSlug { get; set; } = string.Empty;
    /// <summary>One of the seven classifications (CHECK).</summary>
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string MediaType { get; set; } = "text/plain";
    public string ContentSummary { get; set; } = string.Empty;
    /// <summary>Wire digest of the content bytes ("sha256:&lt;64hex&gt;", CHECK).</summary>
    public string ContentHash { get; set; } = string.Empty;
    /// <summary>jsonb provenance document (source, evidenceRef, createdBy, createdAt, optional work/execution/digest).</summary>
    public string ProvenanceJson { get; set; } = "{}";
    public DateTime ObservedAt { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    /// <summary>internal | public (CHECK).</summary>
    public string Classification { get; set; } = "internal";
    /// <summary>private | team-only | facilitator-visible | evidence-only (CHECK).</summary>
    public string PolicyLabel { get; set; } = "team-only";
    /// <summary>candidate | active | superseded | tombstoned (CHECK).</summary>
    public string Status { get; set; } = "candidate";
    public int Revision { get; set; } = 1;
    /// <summary>The entry this revision supersedes; null on the first revision.</summary>
    public Guid? SupersedesEntryId { get; set; }
    public Guid[] ContradictsEntryIds { get; set; } = [];
    /// <summary>Materialized at retain from the scope/Project policy (D7); null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// facilitator.memory_shares — explicit cross-team read grants (D2).
/// One row per (entry, grantee); revocation stamps <c>RevokedAt</c> and a
/// re-grant clears it, so a pair never has duplicate rows.
/// </summary>
public sealed class MemoryShareEntity
{
    public Guid ShareId { get; set; }
    public Guid EntryId { get; set; }
    public string GranteeTeamSlug { get; set; } = string.Empty;
    public string GrantedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// facilitator.memory_usage — the <c>memory.used</c> law as data (
/// D3): one row per entry returned to an automated reader (kernel recall
/// or an external attestation via <c>event.memory.used</c>). Rows survive entry
/// deletion: the audit trail outlives the content.
/// </summary>
public sealed class MemoryUsageEntity
{
    public Guid UsageId { get; set; }
    public Guid EntryId { get; set; }
    /// <summary>Canonical actor string of the consumer.</summary>
    public string Consumer { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public Guid? WorkId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// facilitator.admin_config — the operator configuration store:
/// one row per editable resource, optimistic <c>row_version</c>
/// fenced PATCH (If-Match), same-tx audit + bounded history. Stored intent
/// only: a <c>Config:</c>-file value remains the runtime truth until a
/// feature reads through.
/// </summary>
public sealed class AdminConfigEntity
{
    /// <summary>Resource name (PK, e.g. <c>notifications</c>).</summary>
    public required string Resource { get; set; }

    /// <summary>jsonb document validated against the resource's registered shape.</summary>
    public required string Payload { get; set; }

    /// <summary>Optimistic-concurrency counter (If-Match ETag).</summary>
    public long RowVersion { get; set; }

    /// <summary>Canonical actor of the last writer (oidc:iss|sub).</summary>
    public required string Author { get; set; }

    /// <summary>Change reason (X-Change-Reason, required).</summary>
    public required string Reason { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// facilitator.admin_config_history — bounded per-resource version trail
/// newest 20 versions per resource, written in the same tx as the
/// config row. Rollback = PATCH to a prior payload.
/// </summary>
public sealed class AdminConfigVersionEntity
{
    public required string Resource { get; set; }

    /// <summary>The row_version this snapshot carries.</summary>
    public long Version { get; set; }

    /// <summary>jsonb document, verbatim.</summary>
    public required string Payload { get; set; }

    public required string Author { get; set; }

    public required string Reason { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.provisioning_operations — the durable
/// provisioning ledger. One row per appended launcher operation; the id is
/// the deterministic trigger identity (<see cref="Kernel.Provisioning.ProvisioningOperationId"/>),
/// so a replayed trigger lands the same row and the same outbox
/// <c>command_id</c> (at-least-once enqueue is a no-op). State mirror:
/// requested → running when the command delivered AND the workload's link
/// session is live; requested → stopped when a stop delivered against a
/// non-live workload or the receiver terminally REJECTED the command;
/// requested → failed when the row dead-lettered (delivery budget spent —
/// honest terminal, a fresh work-keyed op may re-fire); running → stopped
/// when the session stays gone across two consecutive sweeps (worker-lost
/// demotion — final for THAT op; the relaunch arrives as a fresh
/// work-keyed op).</summary>
public sealed class ProvisioningOpEntity
{
    /// <summary>PK = deterministic trigger id = the outbox command id.</summary>
    public required string OperationId { get; set; }

    /// <summary>The managed workload (= its link endpoint id).</summary>
    public required string WorkloadId { get; set; }

    /// <summary>The owning team slug (wire-required).</summary>
    public required string Team { get; set; }

    /// <summary>start | stop.</summary>
    public required string Operation { get; set; }

    /// <summary>requested | running | stopped | failed.</summary>
    public required string State { get; set; }

    /// <summary>The triggering work (worker-lost / stalled-scaleup /
    /// no-capacity arms); null for workload-level ops (min-workers,
    /// idle-shutdown).</summary>
    public Guid? WorkId { get; set; }

    /// <summary>Triggering assignment (assignment-keyed arms only).</summary>
    public Guid? AssignmentId { get; set; }

    /// <summary>Generation from the trigger: the FAILED-attempt generation
    /// (worker-lost) or the stalled generation; 0 for the no-capacity tuple
    /// literal; the ledger-row count for workload-level ids.</summary>
    public int? Generation { get; set; }

    /// <summary>worker-lost | stalled-scaleup | no-capacity | min-workers |
    /// idle-shutdown.</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.ai_turns — the AI ledger: one row per AI touch,
/// both directions (in = decoded human/provider message, out = composed
/// reply). Doubles as the conversation-memory source for the context
/// builder and as the audit of what the model saw and said.</summary>
public sealed class AiTurnEntity
{
    public Guid TurnId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>Conversation key: provider|channel|root (provider plane) or
    /// chat:&lt;session-id&gt; (admin chat plane).</summary>
    public required string ConversationKey { get; set; }

    /// <summary>Canonical oidc actor for the chat plane; null on the provider plane.</summary>
    public string? Actor { get; set; }

    /// <summary>Provider account id on the provider plane; null on the chat plane.</summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>in | out.</summary>
    public required string Direction { get; set; }

    public required string Text { get; set; }

    /// <summary>slash | model | fallback — how the turn was decided.</summary>
    public required string Basis { get; set; }

    public string? TemplateId { get; set; }

    /// <summary>The decode/chat contract document, when one was parsed.</summary>
    public string? Decoded { get; set; }

    public string? EndpointName { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public string? TraceId { get; set; }
}

/// <summary>facilitator.actor_preferences — per-actor AI reply verbosity.
/// Precedence: row &gt; admin_config llm.verbosityDefault.</summary>
public sealed class ActorPreferenceEntity
{
    /// <summary>Canonical oidc actor (oidc:iss|sub form).</summary>
    public required string Actor { get; set; }

    /// <summary>terse | balanced | proactive.</summary>
    public required string Verbosity { get; set; }

    public string Extras { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; }
}

/// <summary>facilitator.chat_sessions — an admin chat conversation
/// (browsable history; the chat plane's unit of ownership).</summary>
public sealed class ChatSessionEntity
{
    public Guid SessionId { get; set; }

    /// <summary>Canonical oidc actor of the owner.</summary>
    public required string Actor { get; set; }

    /// <summary>First user turn, truncated to 80 chars.</summary>
    public required string Title { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastMessageAt { get; set; }
}

/// <summary>facilitator.chat_messages — one stored turn of a chat session.</summary>
public sealed class ChatMessageEntity
{
    public Guid MessageId { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>user | assistant.</summary>
    public required string Role { get; set; }

    public required string Text { get; set; }

    /// <summary>model | fallback | slash.</summary>
    public required string Basis { get; set; }

    public string? TemplateId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? TraceId { get; set; }

    public ChatSessionEntity? Session { get; set; }
}

/// <summary>facilitator.chat_actions — one AI-PROPOSED kernel API call,
/// executed only on explicit confirm, under the confirming admin's own
/// bearer token. History rows persist forever; audit points here.</summary>
public sealed class ChatActionEntity
{
    public Guid ActionId { get; set; }

    public Guid SessionId { get; set; }

    public Guid MessageId { get; set; }

    public required string Method { get; set; }

    public required string Path { get; set; }

    /// <summary>Proposed request body (jsonb text; null = no body).</summary>
    public string? Body { get; set; }

    /// <summary>Machine-readable description shown on the action card.</summary>
    public required string Description { get; set; }

    /// <summary>pending | executed | failed | cancelled.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Optimistic single-execution fence (app-incremented, the
    /// admin_config precedent): the claim UPDATE fences on it.</summary>
    public long RowVersion { get; set; }

    public int? StatusCode { get; set; }

    /// <summary>Response body captured on execution (jsonb or text).</summary>
    public string? Result { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? ExecutedBy { get; set; }

    public DateTime? ExecutedAt { get; set; }

    public ChatSessionEntity? Session { get; set; }

    public ChatMessageEntity? Message { get; set; }
}
