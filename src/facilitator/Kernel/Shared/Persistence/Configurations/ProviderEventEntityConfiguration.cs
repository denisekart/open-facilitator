using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.provider_events</summary>
public sealed class ProviderEventEntityConfiguration : IEntityTypeConfiguration<ProviderEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProviderEventEntity> e)
    {
        e.ToTable("provider_events");
        e.HasKey(x => x.ProviderEventId);
        e.Property(x => x.ProviderEventId).HasColumnName("provider_event_id").ValueGeneratedNever();
        e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
        e.Property(x => x.ProviderDeliveryId).HasColumnName("provider_delivery_id");
        e.Property(x => x.BodySha256).HasColumnName("body_sha256").IsRequired();
        e.Property(x => x.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.VerifiedReference).HasColumnName("verified_reference");
        e.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        e.HasIndex(x => new { x.Provider, x.ProviderDeliveryId }).IsUnique()
            .HasDatabaseName("provider_events_provider_delivery_unique")
            .HasFilter("provider IS NOT NULL AND provider_delivery_id IS NOT NULL");
    }
}
