using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.dead_letters</summary>
public sealed class DeadLetterEntityConfiguration : IEntityTypeConfiguration<DeadLetterEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeadLetterEntity> e)
    {
        e.ToTable("dead_letters");
        e.HasKey(x => x.DeadLetterId);
        e.Property(x => x.DeadLetterId).HasColumnName("dead_letter_id").ValueGeneratedNever();
        e.Property(x => x.OutboxId).HasColumnName("outbox_id").IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}
