using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.schedules</summary>
public sealed class ScheduleEntityConfiguration : IEntityTypeConfiguration<ScheduleEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScheduleEntity> e)
    {
        e.ToTable("schedules");
        e.HasKey(x => x.ScheduleId);
        e.Property(x => x.ScheduleId).HasColumnName("schedule_id").ValueGeneratedNever();
        e.Property(x => x.TeamId).HasColumnName("team_id").IsRequired();
        e.Property(x => x.Objective).HasColumnName("objective").IsRequired();
        e.Property(x => x.Cadence).HasColumnName("cadence").IsRequired();
        e.Property(x => x.Timezone).HasColumnName("timezone");
        e.Property(x => x.NextRunAt).HasColumnName("next_run_at").IsRequired();
        e.Property(x => x.LastRunAt).HasColumnName("last_run_at");
        e.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        e.Property(x => x.OwnerActor).HasColumnName("owner_actor");
        e.Property(x => x.CreatedBy).HasColumnName("created_by").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => new { x.Enabled, x.NextRunAt }).HasDatabaseName("schedules_due_idx");
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("schedules_team_id_fkey");
    }
}
