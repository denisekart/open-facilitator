using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.memory_scopes (Plan 16).</summary>
public sealed class MemoryScopeEntityConfiguration : IEntityTypeConfiguration<MemoryScopeEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryScopeEntity> e)
    {
        e.ToTable("memory_scopes");
        e.HasKey(x => x.ScopeId);
        e.Property(x => x.ScopeId).HasColumnName("scope_id").ValueGeneratedNever();
        e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        e.Property(x => x.TeamSlug).HasColumnName("team_slug").IsRequired();
        e.Property(x => x.ProjectId).HasColumnName("project_id");
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.CanonicalSelector).HasColumnName("canonical_selector").IsRequired();
        e.Property(x => x.RetentionDays).HasColumnName("retention_days");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => x.CanonicalSelector).IsUnique().HasDatabaseName("ix_memory_scopes_selector");
        e.HasIndex(x => x.TeamSlug).HasDatabaseName("ix_memory_scopes_team");
        e.ToTable(t => t.HasCheckConstraint("memory_scopes_kind_check",
            CheckSql.InList("kind", ["work", "project", "team", "private"])));
    }
}
