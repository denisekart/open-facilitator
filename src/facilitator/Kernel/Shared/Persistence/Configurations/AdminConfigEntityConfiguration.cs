using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.admin_config + admin_config_history (spec §17.4).</summary>
public sealed class AdminConfigEntityConfiguration :
    IEntityTypeConfiguration<AdminConfigEntity>,
    IEntityTypeConfiguration<AdminConfigVersionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AdminConfigEntity> e)
    {
        e.ToTable("admin_config");
        e.HasKey(x => x.Resource);
        e.Property(x => x.Resource).HasColumnName("resource").HasMaxLength(64);
        e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.RowVersion).HasColumnName("row_version").IsRequired();
        e.Property(x => x.Author).HasColumnName("author").HasMaxLength(512).IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512).IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AdminConfigVersionEntity> e)
    {
        e.ToTable("admin_config_history");
        e.HasKey(x => new { x.Resource, x.Version });
        e.Property(x => x.Resource).HasColumnName("resource").HasMaxLength(64);
        e.Property(x => x.Version).HasColumnName("version").IsRequired();
        e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.Author).HasColumnName("author").HasMaxLength(512).IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512).IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }
}
