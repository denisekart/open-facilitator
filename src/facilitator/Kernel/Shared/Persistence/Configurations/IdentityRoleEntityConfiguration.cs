using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.identity_roles — runtime-mutable role rule sets.</summary>
public sealed class IdentityRoleEntityConfiguration : IEntityTypeConfiguration<IdentityRoleEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityRoleEntity> e)
    {
        e.ToTable("identity_roles");
        e.HasKey(x => x.RoleName);
        e.Property(x => x.RoleName).HasColumnName("role_name").HasMaxLength(200);
        e.Property(x => x.Rules).HasColumnName("rules").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.RowVersion).HasColumnName("row_version").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }
}
