using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.communication_bindings: one binding row per
/// bound conversation (thread/DM). The unique (provider, channel, root) index is the
/// reply-routing lookup; the unique idempotency_key index is the "same post twice ⇒
/// one Work" law.</summary>
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
        e.Property(x => x.TeamSlug).HasColumnName("team");
        e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        // Per-work grain: one teamless binding per conversation, one binding per
        // (conversation, work) — a shared thread can carry two teams' works.
        e.HasIndex(x => new { x.Provider, x.Channel, x.RootMessageId }).IsUnique()
            .HasDatabaseName("communication_bindings_conversation_unique")
            .HasFilter("work_id IS NULL");
        e.HasIndex(x => new { x.Provider, x.Channel, x.RootMessageId, x.WorkId }).IsUnique()
            .HasDatabaseName("communication_bindings_conversation_work_unique")
            .HasFilter("work_id IS NOT NULL");
        e.HasIndex(x => x.IdempotencyKey).IsUnique()
            .HasDatabaseName("communication_bindings_idempotency_unique")
            .HasFilter("idempotency_key IS NOT NULL");
    }
}
