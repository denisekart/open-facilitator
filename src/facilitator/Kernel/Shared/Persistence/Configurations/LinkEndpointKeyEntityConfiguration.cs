using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.link_endpoint_keys: the enrolled public keys that are an
/// endpoint's identity. Exactly one ACTIVE key per enrollment is a database
/// constraint (the partial unique index), not a code convention.</summary>
public sealed class LinkEndpointKeyEntityConfiguration : IEntityTypeConfiguration<LinkEndpointKeyEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkEndpointKeyEntity> e)
    {
        e.ToTable("link_endpoint_keys");
        e.HasKey(x => new { x.EnrollmentId, x.KeyId });
        e.Property(x => x.EnrollmentId).HasColumnName("enrollment_id").IsRequired();
        e.Property(x => x.KeyId).HasColumnName("key_id").IsRequired();
        e.Property(x => x.PublicKey).HasColumnName("public_key").IsRequired();
        e.Property(x => x.KeyAlgorithm).HasColumnName("key_algorithm").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.RegisteredAt).HasColumnName("registered_at").IsRequired();
        e.Property(x => x.RetiredAt).HasColumnName("retired_at");
        e.HasIndex(x => x.EnrollmentId).IsUnique()
            .HasDatabaseName("link_endpoint_keys_one_active_per_enrollment")
            .HasFilter("state = 'active'");
        e.HasIndex(x => x.KeyId).HasDatabaseName("link_endpoint_keys_key_id_idx");
        e.HasOne<LinkEnrollmentEntity>().WithMany()
            .HasForeignKey(x => x.EnrollmentId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("link_endpoint_keys_enrollment_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("link_endpoint_keys_state_check",
            CheckSql.InList("state", ["active", "retired"])));
        e.ToTable(t => t.HasCheckConstraint("link_endpoint_keys_key_algorithm_check",
            CheckSql.InList("key_algorithm", ["ed25519"])));
        e.ToTable(t => t.HasCheckConstraint("link_endpoint_keys_key_id_check",
            CheckSql.IsSha256Digest("key_id")));
    }
}
