using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.approvals — schema now, behavior later.</summary>
public sealed class ApprovalEntityConfiguration : IEntityTypeConfiguration<ApprovalEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApprovalEntity> e)
    {
        e.ToTable("approvals");
        e.HasKey(x => x.ApprovalId);
        e.Property(x => x.ApprovalId).HasColumnName("approval_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.ActionDigest).HasColumnName("action_digest").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.Nonce).HasColumnName("nonce").IsRequired();
        e.Property(x => x.EligibleActors).HasColumnName("eligible_actors").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        e.Property(x => x.DecidedBy).HasColumnName("decided_by");
        e.Property(x => x.ActorEvidenceRef).HasColumnName("actor_evidence_ref");
        e.Property(x => x.Comment).HasColumnName("comment");
        e.Property(x => x.DecidedAt).HasColumnName("decided_at");
        e.Property(x => x.DigestVerification).HasColumnName("digest_verification").IsRequired();
        e.HasIndex(x => x.WorkId).HasDatabaseName("ix_approvals_work_id");
        e.ToTable(t => t.HasCheckConstraint("approvals_state_check",
            CheckSql.InList("state", ["pending", "approved", "denied", "expired", "revoked", "consumed"])));
    }
}
