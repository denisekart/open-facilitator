using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.provider_identities: the unique
/// (provider, tenant, account) key is the mapping identity — redemption upserts.</summary>
public sealed class ProviderIdentityEntityConfiguration : IEntityTypeConfiguration<ProviderIdentityEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProviderIdentityEntity> e)
    {
        e.ToTable("provider_identities");
        e.HasKey(x => x.IdentityId);
        e.Property(x => x.IdentityId).HasColumnName("identity_id").ValueGeneratedNever();
        e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
        e.Property(x => x.Tenant).HasColumnName("tenant").IsRequired();
        e.Property(x => x.AccountId).HasColumnName("account_id").IsRequired();
        e.Property(x => x.OidcIssuer).HasColumnName("oidc_issuer").IsRequired();
        e.Property(x => x.OidcSubject).HasColumnName("oidc_subject").IsRequired();
        e.Property(x => x.ChallengeId).HasColumnName("challenge_id");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => new { x.Provider, x.Tenant, x.AccountId }).IsUnique()
            .HasDatabaseName("provider_identities_account_unique");
    }
}
