using Facilitator.Kernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.link_sessions — §7.3 session leases.</summary>
public sealed class LinkSessionEntityConfiguration : IEntityTypeConfiguration<LinkSessionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkSessionEntity> e)
    {
        e.ToTable("link_sessions");
        e.HasKey(x => x.LogicalEndpointId);
        e.Property(x => x.LogicalEndpointId).HasColumnName("logical_endpoint_id").ValueGeneratedNever();
        e.Property(x => x.ConnectionInstanceId).HasColumnName("connection_instance_id").IsRequired();
        e.Property(x => x.ReplicaId).HasColumnName("replica_id").IsRequired();
        e.Property(x => x.SessionEpoch).HasColumnName("session_epoch").IsRequired();
        e.Property(x => x.LeaseUntil).HasColumnName("lease_until").IsRequired();
        e.Property(x => x.LastHeartbeat).HasColumnName("last_heartbeat").IsRequired();
        e.Property(x => x.CapabilitiesDigest).HasColumnName("capabilities_digest");
    }
}
