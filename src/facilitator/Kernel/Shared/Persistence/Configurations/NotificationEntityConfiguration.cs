using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.notifications — the notification ledger (one addressed
/// human notice per row, whatever channel reaches it).</summary>
public sealed class NotificationEntityConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationEntity> e)
    {
        e.ToTable("notifications");
        e.HasKey(x => x.NotificationId);
        e.Property(x => x.NotificationId).HasColumnName("notification_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.Audience).HasColumnName("audience").IsRequired();
        e.Property(x => x.TeamId).HasColumnName("team_id");
        e.Property(x => x.Actor).HasColumnName("actor");
        e.Property(x => x.Level).HasColumnName("level").IsRequired();
        e.Property(x => x.Text).HasColumnName("text").IsRequired();
        e.Property(x => x.Target).HasColumnName("target").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.Fallback).HasColumnName("fallback").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason");
        e.Property(x => x.CorrectionWindowEndsAt).HasColumnName("correction_window_ends_at");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => new { x.State, x.CreatedAt }).HasDatabaseName("notifications_state_idx");
        e.HasIndex(x => x.WorkId).HasDatabaseName("notifications_work_id_idx");
        e.ToTable(t =>
        {
            t.HasCheckConstraint("notifications_audience_check", CheckSql.InList("audience", ["actor", "team", "operators"]));
            t.HasCheckConstraint("notifications_level_check", CheckSql.InList("level", ["info", "notice", "warning", "critical"]));
            t.HasCheckConstraint("notifications_state_check",
                CheckSql.InList("state", ["pending", "sent", "fell-back", "undeliverable", "obsolete"]));
        });
    }
}
