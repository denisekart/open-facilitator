using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.team_settings — per-scope (team / channel) switch rows.</summary>
public sealed class TeamSettingEntityConfiguration : IEntityTypeConfiguration<TeamSettingEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TeamSettingEntity> e)
    {
        e.ToTable("team_settings");
        e.HasKey(x => new { x.TeamId, x.ScopeKind, x.ScopeId, x.Resource });
        e.Property(x => x.TeamId).HasColumnName("team_id").ValueGeneratedNever();
        e.Property(x => x.ScopeKind).HasColumnName("scope_kind").IsRequired();
        e.Property(x => x.ScopeId).HasColumnName("scope_id").IsRequired();
        e.Property(x => x.Resource).HasColumnName("resource").IsRequired();
        e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.RowVersion).HasColumnName("row_version").IsRequired();
        e.Property(x => x.Author).HasColumnName("author").IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => new { x.Resource, x.ScopeKind, x.ScopeId }).HasDatabaseName("team_settings_lookup_idx");
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("team_settings_team_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("team_settings_scope_kind_check",
            CheckSql.InList("scope_kind", ["team", "channel"])));
    }
}
