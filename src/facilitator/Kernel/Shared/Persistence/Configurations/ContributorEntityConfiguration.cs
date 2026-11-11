using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.contributors</summary>
public sealed class ContributorEntityConfiguration : IEntityTypeConfiguration<ContributorEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ContributorEntity> e)
    {
        e.ToTable("contributors");
        e.HasKey(x => x.ContributorId);
        e.Property(x => x.ContributorId).HasColumnName("contributor_id").ValueGeneratedNever();
        e.Property(x => x.TeamId).HasColumnName("team_id").IsRequired();
        e.Property(x => x.Handle).HasColumnName("handle").IsRequired();
        e.Property(x => x.DisplayName).HasColumnName("display_name");
        e.Property(x => x.Remit).HasColumnName("remit").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.RetiredAt).HasColumnName("retired_at");
        e.Property(x => x.ChangedBy).HasColumnName("changed_by");
        e.Property(x => x.ChangeReason).HasColumnName("change_reason");
        e.HasIndex(x => x.Handle).IsUnique().HasDatabaseName("contributors_handle_key");
        e.HasIndex(x => x.TeamId).HasDatabaseName("contributors_team_id_idx");
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("contributors_team_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("contributors_status_check", CheckSql.InList("status", ["active", "retired"])));
    }
}
