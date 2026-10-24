using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.link_deliveries — durable delivery/receipt ledger.</summary>
public sealed class LinkDeliveryEntityConfiguration : IEntityTypeConfiguration<LinkDeliveryEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkDeliveryEntity> e)
    {
        e.ToTable("link_deliveries");
        e.HasKey(x => x.DeliveryId);
        e.Property(x => x.DeliveryId).HasColumnName("delivery_id").ValueGeneratedNever();
        e.Property(x => x.EndpointId).HasColumnName("endpoint_id").IsRequired();
        e.Property(x => x.SessionEpoch).HasColumnName("session_epoch").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
        e.Property(x => x.AckedAt).HasColumnName("acked_at");
        e.Property(x => x.Receipt).HasColumnName("receipt").HasColumnType("jsonb");
        e.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        e.Property(x => x.RedeliverAfter).HasColumnName("redeliver_after");
        e.HasIndex(x => new { x.EndpointId, x.State }).HasDatabaseName("link_deliveries_endpoint_id_state_idx");
        e.HasOne<OutboxEntity>().WithMany().HasForeignKey(x => x.DeliveryId).HasConstraintName("link_deliveries_delivery_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("link_deliveries_state_check",
            CheckSql.InList("state", ["pending", "in_flight", "acked"])));
    }
}
