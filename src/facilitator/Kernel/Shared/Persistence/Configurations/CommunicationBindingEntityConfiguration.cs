using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.communication_bindings — schema now, behavior later.</summary>
public sealed class CommunicationBindingEntityConfiguration : IEntityTypeConfiguration<CommunicationBindingEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CommunicationBindingEntity> e)
    {
        e.ToTable("communication_bindings");
        e.HasKey(x => x.BindingId);
        e.Property(x => x.BindingId).HasColumnName("binding_id").ValueGeneratedNever();
        e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
        e.Property(x => x.Channel).HasColumnName("channel").IsRequired();
        e.Property(x => x.RootMessageId).HasColumnName("root_message_id").IsRequired();
        e.Property(x => x.ParentMessageId).HasColumnName("parent_message_id");
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}
