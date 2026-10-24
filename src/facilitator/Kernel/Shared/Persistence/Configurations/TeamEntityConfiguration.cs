using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.teams</summary>
public sealed class TeamEntityConfiguration : IEntityTypeConfiguration<TeamEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TeamEntity> e)
    {
        e.ToTable("teams");
        e.HasKey(x => x.TeamId);
        e.Property(x => x.TeamId).HasColumnName("team_id").ValueGeneratedNever();
        e.Property(x => x.Slug).HasColumnName("slug").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("teams_slug_key");
    }
}
