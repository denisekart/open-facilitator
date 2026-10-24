using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.execution_attempts</summary>
public sealed class ExecutionAttemptEntityConfiguration : IEntityTypeConfiguration<ExecutionAttemptEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExecutionAttemptEntity> e)
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
            CheckSql.InList("status", ["starting", "ready", "working", "stopping", "stopped", "failed", "lost"])));
    }
}
