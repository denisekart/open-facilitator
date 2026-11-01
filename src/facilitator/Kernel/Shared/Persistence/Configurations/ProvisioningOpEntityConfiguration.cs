using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.provisioning_operations — Plan 35 C provisioning
/// ledger: the id-keyed operation audit the sweep's dedupe + cap counts read.</summary>
public sealed class ProvisioningOpEntityConfiguration : IEntityTypeConfiguration<ProvisioningOpEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProvisioningOpEntity> e)
    {
        e.ToTable("provisioning_operations");
        e.HasKey(x => x.OperationId);
        e.Property(x => x.OperationId).HasColumnName("operation_id").ValueGeneratedNever();
        e.Property(x => x.WorkloadId).HasColumnName("workload_id").IsRequired();
        e.Property(x => x.Team).HasColumnName("team").IsRequired();
        e.Property(x => x.Operation).HasColumnName("operation").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        e.Property(x => x.Generation).HasColumnName("generation");
        e.Property(x => x.Reason).HasColumnName("reason");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => x.WorkloadId).HasDatabaseName("provisioning_operations_workload_idx");
        e.ToTable(t => t.HasCheckConstraint("provisioning_operations_operation_check",
            CheckSql.InList("operation", ["start", "stop"])));
        e.ToTable(t => t.HasCheckConstraint("provisioning_operations_state_check",
            CheckSql.InList("state", ["requested", "running", "stopped", "failed"])));
    }
}
