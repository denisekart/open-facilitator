using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.inbox — transactional ingress.</summary>
public sealed class InboxEntityConfiguration : IEntityTypeConfiguration<InboxEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InboxEntity> e)
    {
        e.ToTable("inbox");
        e.HasKey(x => x.InboxId);
        e.Property(x => x.InboxId).HasColumnName("inbox_id").ValueGeneratedNever();
        e.Property(x => x.EventSource).HasColumnName("event_source").IsRequired();
        e.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        e.Property(x => x.CommandId).HasColumnName("command_id");
        e.Property(x => x.Provider).HasColumnName("provider");
        e.Property(x => x.ProviderDeliveryId).HasColumnName("provider_delivery_id");
        e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").IsRequired();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.AvailableAt).HasColumnName("available_at").IsRequired();
        e.Property(x => x.LeasedUntil).HasColumnName("leased_until");
        e.Property(x => x.LeaseOwner).HasColumnName("lease_owner");
        e.Property(x => x.Attempt).HasColumnName("attempt").IsRequired();
        e.Property(x => x.Error).HasColumnName("error");
        e.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        e.HasIndex(x => new { x.EventSource, x.EventId }).IsUnique().HasDatabaseName("inbox_event_source_event_id_key");
        e.HasIndex(x => x.CommandId).IsUnique().HasDatabaseName("inbox_command_id_key").HasFilter("command_id IS NOT NULL");
        e.HasIndex(x => new { x.Provider, x.ProviderDeliveryId }).IsUnique()
            .HasDatabaseName("inbox_provider_delivery_unique")
            .HasFilter("provider IS NOT NULL AND provider_delivery_id IS NOT NULL");
        e.HasIndex(x => new { x.AvailableAt, x.LeasedUntil })
            .HasDatabaseName("inbox_due")
            .HasFilter("state = 'pending'");
        e.ToTable(t => t.HasCheckConstraint("inbox_state_check",
            CheckSql.InList("state", ["pending", "processing", "processed", "failed", "duplicate"])));
    }
}
