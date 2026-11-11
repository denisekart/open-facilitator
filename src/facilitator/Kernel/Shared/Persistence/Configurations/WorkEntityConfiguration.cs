using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.works</summary>
public sealed class WorkEntityConfiguration : IEntityTypeConfiguration<WorkEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkEntity> e)
    {
        e.ToTable("works");
        e.HasKey(x => x.WorkId);
        e.Property(x => x.WorkId).HasColumnName("work_id").ValueGeneratedNever();
        e.Property(x => x.ProjectId).HasColumnName("project_id");
        e.Property(x => x.Objective).HasColumnName("objective").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.Version).HasColumnName("version").IsRequired();
        e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
        e.Property(x => x.TeamId).HasColumnName("team_id");
        e.Property(x => x.OwnerActor).HasColumnName("owner_actor");
        e.Property(x => x.Origin).HasColumnName("origin");
        e.Property(x => x.ActiveWhere).HasColumnName("active_where").HasColumnType("jsonb");
        e.Property(x => x.Cost).HasColumnName("cost").HasColumnType("jsonb");
        e.Property(x => x.MergedInto).HasColumnName("merged_into");
        e.Property(x => x.CloseReason).HasColumnName("close_reason");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("works_project_id_fkey");
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("works_team_id_fkey");
        e.HasIndex(x => x.TeamId).HasDatabaseName("works_team_id_idx");
        e.HasIndex(x => x.MergedInto).HasDatabaseName("works_merged_into_idx");
        e.ToTable(t => t.HasCheckConstraint("works_state_check", CheckSql.InList("state", StateGuard.AllWorkWires)));
    }
}
