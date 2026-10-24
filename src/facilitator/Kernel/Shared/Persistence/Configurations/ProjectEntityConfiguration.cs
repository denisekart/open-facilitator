using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.projects</summary>
public sealed class ProjectEntityConfiguration : IEntityTypeConfiguration<ProjectEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProjectEntity> e)
    {
        e.ToTable("projects");
        e.HasKey(x => x.ProjectId);
        e.Property(x => x.ProjectId).HasColumnName("project_id").ValueGeneratedNever();
        e.Property(x => x.TeamId).HasColumnName("team_id").IsRequired();
        e.Property(x => x.Name).HasColumnName("name").IsRequired();
        e.Property(x => x.Config).HasColumnName("config").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("projects_team_id_fkey");
    }
}
