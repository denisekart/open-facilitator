using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.held_actions — the command consent holds (persist-first law D3).</summary>
public sealed class HeldActionEntityConfiguration : IEntityTypeConfiguration<HeldActionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<HeldActionEntity> e)
    {
        e.ToTable("held_actions");
        e.HasKey(x => x.HeldActionId);
        e.Property(x => x.HeldActionId).HasColumnName("held_action_id").ValueGeneratedNever();
        e.Property(x => x.ApprovalId).HasColumnName("approval_id").IsRequired();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.CapabilityId).HasColumnName("capability_id").IsRequired();
        e.Property(x => x.EndpointId).HasColumnName("endpoint_id").IsRequired();
        e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.ActionDigest).HasColumnName("action_digest").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        e.Property(x => x.DecidedBy).HasColumnName("decided_by");
        e.Property(x => x.NudgeAt).HasColumnName("nudge_at");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => x.ApprovalId).IsUnique().HasDatabaseName("held_actions_approval_id_key");
        e.HasIndex(x => new { x.State, x.NudgeAt }).HasDatabaseName("held_actions_sweep_idx");
        e.HasOne<ApprovalEntity>().WithOne().HasForeignKey<HeldActionEntity>(x => x.ApprovalId)
            .HasConstraintName("held_actions_approval_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("held_actions_state_check",
            CheckSql.InList("state", ["pending", "approved", "denied", "expired", "revoked", "delivered"])));
    }
}
