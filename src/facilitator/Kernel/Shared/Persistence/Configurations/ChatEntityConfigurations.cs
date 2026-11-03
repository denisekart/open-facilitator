using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.chat_sessions / chat_messages / chat_actions — the
/// admin chat plane: stored history + confirm-before-action proposals.</summary>
public sealed class ChatEntityConfigurations
    : IEntityTypeConfiguration<ChatSessionEntity>,
      IEntityTypeConfiguration<ChatMessageEntity>,
      IEntityTypeConfiguration<ChatActionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatSessionEntity> e)
    {
        e.ToTable("chat_sessions");
        e.HasKey(x => x.SessionId);
        e.Property(x => x.SessionId).HasColumnName("session_id").ValueGeneratedNever();
        e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(512).IsRequired();
        e.Property(x => x.Title).HasColumnName("title").HasMaxLength(120).IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.LastMessageAt).HasColumnName("last_message_at").IsRequired();
        e.HasIndex(x => x.LastMessageAt).HasDatabaseName("ix_chat_sessions_last_message_at");
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatMessageEntity> e)
    {
        e.ToTable("chat_messages");
        e.HasKey(x => x.MessageId);
        e.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        e.Property(x => x.Role).HasColumnName("role").HasMaxLength(16).IsRequired();
        e.Property(x => x.Text).HasColumnName("text").IsRequired();
        e.Property(x => x.Basis).HasColumnName("basis").HasMaxLength(16).IsRequired();
        e.Property(x => x.TemplateId).HasColumnName("template_id").HasMaxLength(64);
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.TraceId).HasColumnName("trace_id").HasMaxLength(64);
        e.ToTable(t => t.HasCheckConstraint("chat_messages_role_chk", "role IN ('user','assistant')"));
        e.ToTable(t => t.HasCheckConstraint("chat_messages_basis_chk", "basis IN ('model','fallback','slash')"));
        e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId)
            .HasConstraintName("chat_messages_session_id_fkey").OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.SessionId, x.CreatedAt }).HasDatabaseName("ix_chat_messages_session_id_created_at");
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatActionEntity> e)
    {
        e.ToTable("chat_actions");
        e.HasKey(x => x.ActionId);
        e.Property(x => x.ActionId).HasColumnName("action_id").ValueGeneratedNever();
        e.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        e.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
        e.Property(x => x.Method).HasColumnName("method").HasMaxLength(10).IsRequired();
        e.Property(x => x.Path).HasColumnName("path").HasMaxLength(512).IsRequired();
        e.Property(x => x.Body).HasColumnName("body").HasColumnType("jsonb");
        e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000).IsRequired();
        e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        e.Property(x => x.RowVersion).HasColumnName("row_version").IsRequired();
        e.Property(x => x.StatusCode).HasColumnName("status_code");
        e.Property(x => x.Result).HasColumnName("result");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ExecutedBy).HasColumnName("executed_by").HasMaxLength(512);
        e.Property(x => x.ExecutedAt).HasColumnName("executed_at");
        e.ToTable(t => t.HasCheckConstraint("chat_actions_status_chk", "status IN ('pending','executed','failed','cancelled')"));
        e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId)
            .HasConstraintName("chat_actions_session_id_fkey").OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId)
            .HasConstraintName("chat_actions_message_id_fkey").OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.Status).HasDatabaseName("ix_chat_actions_status");
    }
}
