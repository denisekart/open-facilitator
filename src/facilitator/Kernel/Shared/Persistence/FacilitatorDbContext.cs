
using Microsoft.EntityFrameworkCore;
// ADR: Why this schema looks like this —
//  * `inbox`/`outbox` carry their own lease + attempt columns so claims run as
//    short `FOR UPDATE SKIP LOCKED` transactions; the dispatcher calls sinks
//    OUTSIDE any transaction (no remote I/O inside a long tx).
//  * `works.version` is the optimistic fence: every state change is a
//    version-checked UPDATE inside a per-Work `pg_advisory_xact_lock`, so two
//    concurrent causal decisions cannot overwrite one another.
//  * `NOTIFY facilitator_outbox` is a HINT only: correctness comes from
//    the polling claim query, never from the wake.
//  * Tables beyond the original ingress/dispatch write set (link_sessions,
//    approvals, communication_bindings, teams/projects/assignments
//    partially) land in the SAME InitialCreate baseline so no later feature
//    needs a disruptive re-baseline.
namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>The single transactional foundation, schema <c>facilitator</c>.</summary>
public class FacilitatorDbContext(DbContextOptions<FacilitatorDbContext> options) : DbContext(options)
{
    public DbSet<TeamEntity> Teams => Set<TeamEntity>();

    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();

    public DbSet<WorkEntity> Works => Set<WorkEntity>();

    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();

    public DbSet<ExecutionAttemptEntity> ExecutionAttempts => Set<ExecutionAttemptEntity>();

    public DbSet<WorkEventEntity> WorkEvents => Set<WorkEventEntity>();

    public DbSet<InboxEntity> Inbox => Set<InboxEntity>();

    public DbSet<OutboxEntity> Outbox => Set<OutboxEntity>();

    public DbSet<DeadLetterEntity> DeadLetters => Set<DeadLetterEntity>();

    public DbSet<ProviderEventEntity> ProviderEvents => Set<ProviderEventEntity>();

    public DbSet<LinkSessionEntity> LinkSessions => Set<LinkSessionEntity>();

    public DbSet<LinkEnrollmentEntity> LinkEnrollments => Set<LinkEnrollmentEntity>();

    public DbSet<EnrollmentTokenEntity> EnrollmentTokens => Set<EnrollmentTokenEntity>();

    public DbSet<LinkDeliveryEntity> LinkDeliveries => Set<LinkDeliveryEntity>();

    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();

    public DbSet<CommunicationBindingEntity> CommunicationBindings => Set<CommunicationBindingEntity>();

    public DbSet<ProviderIdentityEntity> ProviderIdentities => Set<ProviderIdentityEntity>();

    public DbSet<ProviderLinkChallengeEntity> ProviderLinkChallenges => Set<ProviderLinkChallengeEntity>();

    public DbSet<AuditEventEntity> Audit => Set<AuditEventEntity>();

    public DbSet<IdentityRoleEntity> IdentityRoles => Set<IdentityRoleEntity>();

    public DbSet<EvidenceEntity> Evidence => Set<EvidenceEntity>();

    public DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();

    public DbSet<ResultManifestEntity> ResultManifests => Set<ResultManifestEntity>();

    public DbSet<MemoryScopeEntity> MemoryScopes => Set<MemoryScopeEntity>();

    public DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();

    public DbSet<MemoryShareEntity> MemoryShares => Set<MemoryShareEntity>();

    public DbSet<MemoryUsageEntity> MemoryUsage => Set<MemoryUsageEntity>();

    public DbSet<AdminConfigEntity> AdminConfig => Set<AdminConfigEntity>();

    public DbSet<AdminConfigVersionEntity> AdminConfigHistory => Set<AdminConfigVersionEntity>();

    public DbSet<RoutingDecisionEntity> RoutingDecisions => Set<RoutingDecisionEntity>();

    public DbSet<NudgeLedgerEntity> Nudges => Set<NudgeLedgerEntity>();

    public DbSet<ProvisioningOpEntity> ProvisioningOps => Set<ProvisioningOpEntity>();

    public DbSet<AiTurnEntity> AiTurns => Set<AiTurnEntity>();

    public DbSet<ActorPreferenceEntity> ActorPreferences => Set<ActorPreferenceEntity>();

    public DbSet<ChatSessionEntity> ChatSessions => Set<ChatSessionEntity>();

    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();

    public DbSet<ChatActionEntity> ChatActions => Set<ChatActionEntity>();


    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("facilitator");
        model.ApplyConfigurationsFromAssembly(typeof(FacilitatorDbContext).Assembly);
    }
}
