using Microsoft.EntityFrameworkCore;

// ADR (spec §7.2/§7.4, §19.3): Why this schema looks like this —
//  * `inbox`/`outbox` carry their own lease + attempt columns so claims run as
//    short `FOR UPDATE SKIP LOCKED` transactions; the dispatcher calls sinks
//    OUTSIDE any transaction (no remote I/O inside a long tx, §19.3).
//  * `works.version` is the optimistic fence (§7.4): every state change is a
//    version-checked UPDATE inside a per-Work `pg_advisory_xact_lock`, so two
//    concurrent causal decisions cannot overwrite one another.
//  * `NOTIFY facilitator_outbox` is a HINT only (§7.3): correctness comes from
//    the polling claim query, never from the wake.
//  * Tables beyond Plan 02's write set (link_sessions → Plan 03, approvals →
//    Plan 08, communication_bindings → Plan 13, teams/projects/assignments
//    partially) land in the SAME InitialCreate baseline so no later plan needs
//    a disruptive re-baseline.
namespace Facilitator.Kernel.Persistence;

/// <summary>The single transactional foundation (spec §19), schema <c>facilitator</c>.</summary>
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

    public DbSet<LinkDeliveryEntity> LinkDeliveries => Set<LinkDeliveryEntity>();

    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();

    public DbSet<CommunicationBindingEntity> CommunicationBindings => Set<CommunicationBindingEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("facilitator");
        model.ApplyConfigurationsFromAssembly(typeof(FacilitatorDbContext).Assembly);
    }
}
