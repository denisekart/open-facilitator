using Facilitator.Kernel.Work;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.assignments</summary>
public sealed class AssignmentEntityConfiguration : IEntityTypeConfiguration<AssignmentEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssignmentEntity> e)
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
        e.ToTable(t => t.HasCheckConstraint("assignments_status_check", CheckSql.InList("status", StateGuard.AllAssignmentWires)));
    }
}
