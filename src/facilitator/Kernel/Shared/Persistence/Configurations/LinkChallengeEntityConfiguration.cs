using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.link_challenges: single-use nonces for proofs of
/// possession. Proof jti replay records ride the same table under a
/// <c>link-proof:</c> nonce prefix — one replay mechanism, not two. The expiry
/// index serves the sweeper that deletes rows past <c>expires_at</c>.</summary>
public sealed class LinkChallengeEntityConfiguration : IEntityTypeConfiguration<LinkChallengeEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkChallengeEntity> e)
    {
        e.ToTable("link_challenges");
        e.HasKey(x => x.Nonce);
        e.Property(x => x.Nonce).HasColumnName("nonce").IsRequired();
        e.Property(x => x.EndpointId).HasColumnName("endpoint_id").IsRequired();
        e.Property(x => x.IssuedAt).HasColumnName("issued_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.Consumed).HasColumnName("consumed").IsRequired();
        e.HasIndex(x => x.ExpiresAt).HasDatabaseName("link_challenges_expires_idx");
    }
}
