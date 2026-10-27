using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.work_events</summary>
public sealed class WorkEventEntityConfiguration : IEntityTypeConfiguration<WorkEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkEventEntity> e)
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
            CheckSql.InList("kind",
                ["created", "state.transition", "steer", "command.accepted", "assignment.transition", "approval.decided",
                    "artifact.stored", "artifact.registered", "artifact.available", "artifact.gc"])));
    }
}
