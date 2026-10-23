using Facilitator.Kernel.Domain;

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

    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();

    public DbSet<CommunicationBindingEntity> CommunicationBindings => Set<CommunicationBindingEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("facilitator");

        model.Entity<TeamEntity>(e =>
        {
            e.ToTable("teams");
            e.HasKey(x => x.TeamId);
            e.Property(x => x.TeamId).HasColumnName("team_id").ValueGeneratedNever();
            e.Property(x => x.Slug).HasColumnName("slug").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("teams_slug_key");
        });

        model.Entity<ProjectEntity>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.ProjectId).HasColumnName("project_id").ValueGeneratedNever();
            e.Property(x => x.TeamId).HasColumnName("team_id").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Config).HasColumnName("config").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("projects_team_id_fkey");
        });

        model.Entity<WorkEntity>(e =>
        {
            e.ToTable("works");
            e.HasKey(x => x.WorkId);
            e.Property(x => x.WorkId).HasColumnName("work_id").ValueGeneratedNever();
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Objective).HasColumnName("objective").IsRequired();
            e.Property(x => x.State).HasColumnName("state").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsRequired();
            e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("works_project_id_fkey");
            e.ToTable(t => t.HasCheckConstraint("works_state_check", InList("state", StateGuard.AllWorkWires)));
        });

        model.Entity<AssignmentEntity>(e =>
        {
            e.ToTable("assignments");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.AssignmentId).HasColumnName("assignment_id").ValueGeneratedNever();
            e.Property(x => x.WorkId).HasColumnName("work_id").IsRequired();
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.CapabilityId).HasColumnName("capability_id");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.CurrentExecutionGeneration).HasColumnName("current_execution_generation").IsRequired();
            e.Property(x => x.ExpectedContextVersion).HasColumnName("expected_context_version");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.HasOne(x => x.Work).WithMany().HasForeignKey(x => x.WorkId).HasConstraintName("assignments_work_id_fkey");
            e.ToTable(t => t.HasCheckConstraint("assignments_status_check", InList("status", StateGuard.AllAssignmentWires)));
        });

        model.Entity<ExecutionAttemptEntity>(e =>
        {
            e.ToTable("execution_attempts");
            e.HasKey(x => x.ExecutionId);
            e.Property(x => x.ExecutionId).HasColumnName("execution_id").ValueGeneratedNever();
            e.Property(x => x.AssignmentId).HasColumnName("assignment_id").IsRequired();
            e.Property(x => x.Generation).HasColumnName("generation").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId).HasConstraintName("execution_attempts_assignment_id_fkey");
            e.ToTable(t => t.HasCheckConstraint("execution_attempts_status_check",
                InList("status", ["starting", "ready", "working", "stopping", "stopped", "failed", "lost"])));
        });

        model.Entity<WorkEventEntity>(e =>
        {
            e.ToTable("work_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
            e.Property(x => x.WorkId).HasColumnName("work_id").IsRequired();
            e.Property(x => x.WorkVersion).HasColumnName("work_version").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.HasIndex(x => new { x.WorkId, x.CreatedAt }).HasDatabaseName("work_events_work_id_created_at_idx");
            e.HasOne(x => x.Work).WithMany().HasForeignKey(x => x.WorkId).HasConstraintName("work_events_work_id_fkey");
            e.ToTable(t => t.HasCheckConstraint("work_events_kind_check",
                InList("kind", ["created", "state.transition", "steer", "command.accepted", "assignment.transition"])));
        });

        model.Entity<InboxEntity>(e =>
        {
            e.ToTable("inbox");
            e.HasKey(x => x.InboxId);
            e.Property(x => x.InboxId).HasColumnName("inbox_id").ValueGeneratedNever();
            e.Property(x => x.EventSource).HasColumnName("event_source").IsRequired();
            e.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
            e.Property(x => x.CommandId).HasColumnName("command_id");
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.ProviderDeliveryId).HasColumnName("provider_delivery_id");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").IsRequired();
            e.Property(x => x.WorkId).HasColumnName("work_id");
            e.Property(x => x.State).HasColumnName("state").IsRequired();
            e.Property(x => x.AvailableAt).HasColumnName("available_at").IsRequired();
            e.Property(x => x.LeasedUntil).HasColumnName("leased_until");
            e.Property(x => x.LeaseOwner).HasColumnName("lease_owner");
            e.Property(x => x.Attempt).HasColumnName("attempt").IsRequired();
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.HasIndex(x => new { x.EventSource, x.EventId }).IsUnique().HasDatabaseName("inbox_event_source_event_id_key");
            e.HasIndex(x => x.CommandId).IsUnique().HasDatabaseName("inbox_command_id_key").HasFilter("command_id IS NOT NULL");
            e.HasIndex(x => new { x.Provider, x.ProviderDeliveryId }).IsUnique()
                .HasDatabaseName("inbox_provider_delivery_unique")
                .HasFilter("provider IS NOT NULL AND provider_delivery_id IS NOT NULL");
            e.HasIndex(x => new { x.AvailableAt, x.LeasedUntil })
                .HasDatabaseName("inbox_due")
                .HasFilter("state = 'pending'");
            e.ToTable(t => t.HasCheckConstraint("inbox_state_check",
                InList("state", ["pending", "processing", "processed", "failed", "duplicate"])));
        });

        model.Entity<OutboxEntity>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.OutboxId);
            e.Property(x => x.OutboxId).HasColumnName("outbox_id").ValueGeneratedNever();
            e.Property(x => x.RecipientEndpointId).HasColumnName("recipient_endpoint_id").IsRequired();
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type").IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.CommandId).HasColumnName("command_id").IsRequired();
            e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.WorkId).HasColumnName("work_id");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.AvailableAt).HasColumnName("available_at").IsRequired();
            e.Property(x => x.LeaseOwner).HasColumnName("lease_owner");
            e.Property(x => x.LeasedUntil).HasColumnName("leased_until");
            e.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.HasIndex(x => x.CommandId).IsUnique().HasDatabaseName("outbox_command_id_key");
            e.HasIndex(x => x.WorkId).HasDatabaseName("outbox_work_id_idx");
            e.HasIndex(x => new { x.AvailableAt, x.LeasedUntil })
                .HasDatabaseName("outbox_due")
                .HasFilter("status = 'pending'");
            e.HasIndex(x => x.LeasedUntil)
                .HasDatabaseName("outbox_leased")
                .HasFilter("status = 'leased'");
            e.ToTable(t => t.HasCheckConstraint("outbox_status_check",
                InList("status", ["pending", "leased", "delivered", "dead_letter"])));
            e.ToTable(t => t.HasCheckConstraint("outbox_aggregate_type_check",
                InList("aggregate_type", ["work", "assignment"])));
        });

        model.Entity<DeadLetterEntity>(e =>
        {
            e.ToTable("dead_letters");
            e.HasKey(x => x.DeadLetterId);
            e.Property(x => x.DeadLetterId).HasColumnName("dead_letter_id").ValueGeneratedNever();
            e.Property(x => x.OutboxId).HasColumnName("outbox_id").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").IsRequired();
            e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        });

        model.Entity<ProviderEventEntity>(e =>
        {
            e.ToTable("provider_events");
            e.HasKey(x => x.ProviderEventId);
            e.Property(x => x.ProviderEventId).HasColumnName("provider_event_id").ValueGeneratedNever();
            e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
            e.Property(x => x.ProviderDeliveryId).HasColumnName("provider_delivery_id");
            e.Property(x => x.BodySha256).HasColumnName("body_sha256").IsRequired();
            e.Property(x => x.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.VerifiedReference).HasColumnName("verified_reference");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            e.HasIndex(x => new { x.Provider, x.ProviderDeliveryId }).IsUnique()
                .HasDatabaseName("provider_events_provider_delivery_unique")
                .HasFilter("provider IS NOT NULL AND provider_delivery_id IS NOT NULL");
        });

        model.Entity<LinkSessionEntity>(e =>
        {
            e.ToTable("link_sessions");
            e.HasKey(x => x.LogicalEndpointId);
            e.Property(x => x.LogicalEndpointId).HasColumnName("logical_endpoint_id").ValueGeneratedNever();
            e.Property(x => x.ConnectionInstanceId).HasColumnName("connection_instance_id").IsRequired();
            e.Property(x => x.ReplicaId).HasColumnName("replica_id").IsRequired();
            e.Property(x => x.SessionEpoch).HasColumnName("session_epoch").IsRequired();
            e.Property(x => x.LeaseUntil).HasColumnName("lease_until").IsRequired();
            e.Property(x => x.LastHeartbeat).HasColumnName("last_heartbeat").IsRequired();
            e.Property(x => x.CapabilitiesDigest).HasColumnName("capabilities_digest");
        });

        model.Entity<ApprovalEntity>(e =>
        {
            e.ToTable("approvals");
            e.HasKey(x => x.ApprovalId);
            e.Property(x => x.ApprovalId).HasColumnName("approval_id").ValueGeneratedNever();
            e.Property(x => x.WorkId).HasColumnName("work_id");
            e.Property(x => x.ActionDigest).HasColumnName("action_digest").IsRequired();
            e.Property(x => x.State).HasColumnName("state").IsRequired();
            e.Property(x => x.Nonce).HasColumnName("nonce").IsRequired();
            e.Property(x => x.EligibleActors).HasColumnName("eligible_actors").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            e.ToTable(t => t.HasCheckConstraint("approvals_state_check",
                InList("state", ["pending", "approved", "denied", "expired", "revoked", "consumed"])));
        });

        model.Entity<CommunicationBindingEntity>(e =>
        {
            e.ToTable("communication_bindings");
            e.HasKey(x => x.BindingId);
            e.Property(x => x.BindingId).HasColumnName("binding_id").ValueGeneratedNever();
            e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
            e.Property(x => x.Channel).HasColumnName("channel").IsRequired();
            e.Property(x => x.RootMessageId).HasColumnName("root_message_id").IsRequired();
            e.Property(x => x.ParentMessageId).HasColumnName("parent_message_id");
            e.Property(x => x.WorkId).HasColumnName("work_id");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        });
    }

    private static string InList(string column, IEnumerable<string> allowed) =>
        $"{column} IN ({string.Join(", ", allowed.Select(static v => $"'{v}'"))})";
}
