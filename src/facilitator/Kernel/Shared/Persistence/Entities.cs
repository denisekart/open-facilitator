namespace Facilitator.Kernel.Shared.Persistence;

// Table-backed entity classes (schema `facilitator`, see FacilitatorDbContext
// for the ADR header). Guid ids are generated client-side; all timestamps are
// DateTime in UTC (Npgsql legacy timestamp behaviour is NOT enabled).

/// <summary>facilitator.teams — org unit; rows land with Plan 03+ routing.</summary>
public sealed class TeamEntity
{
    public Guid TeamId { get; set; }

    public required string Slug { get; set; }

    public DateTime CreatedAt { get; set; }
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

/// <summary>facilitator.works — the §4.1 Work aggregate root.</summary>
public sealed class WorkEntity
{
    public Guid WorkId { get; set; }

    public Guid? ProjectId { get; set; }

    public required string Objective { get; set; }

    /// <summary>Wire state name (CHECK-constrained to the §4.1 set).</summary>
    public required string State { get; set; }

    /// <summary>Optimistic-fencing version (spec §7.4); bumped atomically with every state change.</summary>
    public long Version { get; set; }

    /// <summary>Inbox row that created this Work (lineage).</summary>
    public Guid? SourceEventId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ProjectEntity? Project { get; set; }
}

/// <summary>facilitator.assignments — §4.1 Assignment aggregate (rows written from Plan 03).</summary>
public sealed class AssignmentEntity
{
    public Guid AssignmentId { get; set; }

    public Guid WorkId { get; set; }

    public Guid? TeamId { get; set; }

    public string? CapabilityId { get; set; }

    public required string Status { get; set; }

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

/// <summary>facilitator.inbox — transactional ingress (§7.1, §19.2 superset).</summary>
public sealed class InboxEntity
{
    public Guid InboxId { get; set; }

    public required string EventSource { get; set; }

    public required string EventId { get; set; }

    /// <summary>Business-command dedupe key (unique when present, §7.1).</summary>
    public string? CommandId { get; set; }

    public string? Provider { get; set; }

    public string? ProviderDeliveryId { get; set; }

    /// <summary>jsonb document (the full source envelope/body).</summary>
    public required string Payload { get; set; }

    /// <summary>sha256 hex of the exact wire bytes (§7.1 provider-path audit).</summary>
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

/// <summary>facilitator.outbox — transactional egress (§7.2, appendix §5.2).</summary>
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

/// <summary>facilitator.dead_letters — appendix §5.6: diagnostics + original CloudEvent.</summary>
public sealed class DeadLetterEntity
{
    public Guid DeadLetterId { get; set; }

    public Guid OutboxId { get; set; }

    public required string Reason { get; set; }

    /// <summary>jsonb document — original CloudEvent snapshot.</summary>
    public required string Envelope { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>facilitator.provider_events — §7.1 audit of the full provider source.</summary>
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

/// <summary>facilitator.link_sessions — §7.3 session leases (schema-only; behaviour in Plan 03).</summary>
public sealed class LinkSessionEntity
{
    public required string LogicalEndpointId { get; set; }

    public required string ConnectionInstanceId { get; set; }

    public required string ReplicaId { get; set; }

    public long SessionEpoch { get; set; }

    public DateTime LeaseUntil { get; set; }

    public DateTime LastHeartbeat { get; set; }

    public string? CapabilitiesDigest { get; set; }
}

/// <summary>facilitator.link_enrollments — Plan 03 enrollment records (appendix §4).</summary>
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

    /// <summary>gateway | child — role bound at enrollment; rotation never re-roles (Plan 10).</summary>
    public required string Role { get; set; }

    /// <summary>True when this enrollment accepts oidc: delegated evidence claims (mode (b) bindings only).</summary>
    public bool AcceptsDelegatedClaims { get; set; }

    /// <summary>Mode (b): issuer of the workload token that enrolled this endpoint (null for mode (a)).</summary>
    public string? WorkloadIssuer { get; set; }

    /// <summary>Mode (b): subject (client id / service account) of the workload token.</summary>
    public string? WorkloadSubject { get; set; }
}

/// <summary>facilitator.link_deliveries — Plan 03 durable delivery/receipt ledger (S:1336 delivery_receipts).</summary>
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

/// <summary>facilitator.approvals — Plan 11 lifecycle authority (record/respond/consume/revoke).</summary>
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

    /// <summary>Plan 12: the Link endpoint (Gateway) that recorded this approval;
    /// decided-push addressing + consume authorization. Null for non-Link recorders.</summary>
    public string? RequestedByEndpointId { get; set; }
}

/// <summary>facilitator.communication_bindings — Plan 13 behaviour, schema now.</summary>
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

/// <summary>facilitator.audit — every authorization-gate decision, allow and deny (appendix §3.2 step 5).</summary>
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

/// <summary>facilitator.evidence — kernel-issued evidence references (Plan 10 validation, Plan 11 mint/consume).</summary>
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
