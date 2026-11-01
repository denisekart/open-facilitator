using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.nudges — bounded-nudge ledger (§5.3): one row per
/// (assignment, generation, stall episode); cap rides <c>nudge_count</c>.</summary>
public sealed class NudgeLedgerEntityConfiguration : IEntityTypeConfiguration<NudgeLedgerEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NudgeLedgerEntity> e)
    {
        e.ToTable("nudges");
        e.HasKey(x => new { x.AssignmentId, x.Generation, x.StallEpisode });
        e.Property(x => x.AssignmentId).HasColumnName("assignment_id").ValueGeneratedNever();
        e.Property(x => x.Generation).HasColumnName("generation").ValueGeneratedNever();
        e.Property(x => x.StallEpisode).HasColumnName("stall_episode").ValueGeneratedNever();
        e.Property(x => x.NudgeCount).HasColumnName("nudge_count").IsRequired();
        e.Property(x => x.LastNudgeAt).HasColumnName("last_nudge_at").IsRequired();
        e.Property(x => x.LastProgressAt).HasColumnName("last_progress_at").IsRequired();
        e.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId)
            .HasConstraintName("nudges_assignment_id_fkey").OnDelete(DeleteBehavior.Cascade);
    }
}
