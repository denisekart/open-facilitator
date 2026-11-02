using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.provider_link_challenges: single-use codes.
/// Redemption looks a code up by its sha256; the partial index keeps the hot
/// path (unredeemed codes) tight.</summary>
public sealed class ProviderLinkChallengeEntityConfiguration : IEntityTypeConfiguration<ProviderLinkChallengeEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProviderLinkChallengeEntity> e)
    {
        e.ToTable("provider_link_challenges");
        e.HasKey(x => x.ChallengeId);
        e.Property(x => x.ChallengeId).HasColumnName("challenge_id").ValueGeneratedNever();
        e.Property(x => x.CodeSha256).HasColumnName("code_sha256").IsRequired();
        e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
        e.Property(x => x.Tenant).HasColumnName("tenant").IsRequired();
        e.Property(x => x.AccountId).HasColumnName("account_id").IsRequired();
        e.Property(x => x.DeliverToAccountId).HasColumnName("deliver_to_account_id").IsRequired();
        e.Property(x => x.OidcIssuer).HasColumnName("oidc_issuer");
        e.Property(x => x.OidcSubject).HasColumnName("oidc_subject");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.RedeemedAt).HasColumnName("redeemed_at");
        e.HasIndex(x => x.CodeSha256)
            .HasDatabaseName("provider_link_challenges_code_idx");
        e.HasIndex(x => new { x.Provider, x.Tenant, x.AccountId })
            .HasDatabaseName("provider_link_challenges_account_idx");
    }
}
