using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.team_members — the team roster.</summary>
public sealed class TeamMemberEntityConfiguration : IEntityTypeConfiguration<TeamMemberEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TeamMemberEntity> e)
    {
        e.ToTable("team_members");
        e.HasKey(x => new { x.TeamId, x.Actor });
        e.Property(x => x.TeamId).HasColumnName("team_id").ValueGeneratedNever();
        e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
        e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        e.Property(x => x.Capacity).HasColumnName("capacity").IsRequired();
        e.Property(x => x.AddedBy).HasColumnName("added_by");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => x.Actor).HasDatabaseName("team_members_actor_idx");
        e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("team_members_team_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("team_members_kind_check", CheckSql.InList("kind", ["human", "contributor"])));
        e.ToTable(t => t.HasCheckConstraint("team_members_capacity_check", CheckSql.InList("capacity", ["member", "operator"])));
    }
}
