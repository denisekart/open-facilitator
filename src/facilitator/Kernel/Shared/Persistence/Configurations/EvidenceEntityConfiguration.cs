using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.evidence — kernel-issued evidence references (appendix §3.2 kernel: scheme).</summary>
public sealed class EvidenceEntityConfiguration : IEntityTypeConfiguration<EvidenceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EvidenceEntity> e)
    {
        e.ToTable("evidence");
        e.HasKey(x => x.EvidenceId);
        e.Property(x => x.EvidenceId).HasColumnName("evidence_id").ValueGeneratedNever();
        e.Property(x => x.Subject).HasColumnName("subject").IsRequired();
        e.Property(x => x.Claims).HasColumnName("claims").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.IssuedAt).HasColumnName("issued_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
    }
}
