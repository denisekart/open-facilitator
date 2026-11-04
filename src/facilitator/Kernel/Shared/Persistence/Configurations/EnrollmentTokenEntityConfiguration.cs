using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.enrollment_tokens — admin-minted, consumable enrollment tokens.</summary>
public sealed class EnrollmentTokenEntityConfiguration : IEntityTypeConfiguration<EnrollmentTokenEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EnrollmentTokenEntity> e)
    {
        e.ToTable("enrollment_tokens");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        e.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
        e.Property(x => x.Label).HasColumnName("label").IsRequired();
        e.Property(x => x.Team).HasColumnName("team").IsRequired();
        e.Property(x => x.Role).HasColumnName("role").IsRequired();
        e.Property(x => x.CreatedBy).HasColumnName("created_by");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        e.Property(x => x.ConsumedEndpointId).HasColumnName("consumed_endpoint_id");
        e.Property(x => x.LinkEnrollmentId).HasColumnName("link_enrollment_id");
        e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("enrollment_tokens_token_hash_key");
        e.ToTable(t => t.HasCheckConstraint("enrollment_tokens_role_check",
            CheckSql.InList("role", ["gateway", "child"])));
    }
}
