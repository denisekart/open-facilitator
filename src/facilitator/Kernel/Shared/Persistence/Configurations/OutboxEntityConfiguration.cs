using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.outbox — transactional egress.</summary>
public sealed class OutboxEntityConfiguration : IEntityTypeConfiguration<OutboxEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutboxEntity> e)
    {
        e.ToTable("outbox");
        e.HasKey(x => x.OutboxId);
        e.Property(x => x.OutboxId).HasColumnName("outbox_id").ValueGeneratedNever();
        e.Property(x => x.RecipientEndpointId).HasColumnName("recipient_endpoint_id").IsRequired();
        e.Property(x => x.AggregateType).HasColumnName("aggregate_type").IsRequired();
        e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
        e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
        e.Property(x => x.CommandId).HasColumnName("command_id").IsRequired();
        e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.AvailableAt).HasColumnName("available_at").IsRequired();
        e.Property(x => x.LeaseOwner).HasColumnName("lease_owner");
        e.Property(x => x.LeasedUntil).HasColumnName("leased_until");
        e.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        e.Property(x => x.LastError).HasColumnName("last_error");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        e.HasIndex(x => x.CommandId).IsUnique().HasDatabaseName("outbox_command_id_key");
        e.HasIndex(x => x.WorkId).HasDatabaseName("outbox_work_id_idx");
        e.HasIndex(x => new { x.AvailableAt, x.LeasedUntil })
            .HasDatabaseName("outbox_due")
            .HasFilter("status = 'pending'");
        e.HasIndex(x => x.LeasedUntil)
            .HasDatabaseName("outbox_leased")
            .HasFilter("status = 'leased'");
        e.ToTable(t => t.HasCheckConstraint("outbox_status_check",
            CheckSql.InList("status", ["pending", "leased", "delivered", "dead_letter"])));
        e.ToTable(t => t.HasCheckConstraint("outbox_aggregate_type_check",
            CheckSql.InList("aggregate_type", ["work", "assignment"])));
    }
}
