using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.ai_turns — the AI ledger + conversation memory.
/// Conversation-key ordering is the context builder's recency read.</summary>
public sealed class AiTurnEntityConfiguration : IEntityTypeConfiguration<AiTurnEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AiTurnEntity> e)
    {
        e.ToTable("ai_turns");
        e.HasKey(x => x.TurnId);
        e.Property(x => x.TurnId).HasColumnName("turn_id").ValueGeneratedNever();
        e.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        e.Property(x => x.ConversationKey).HasColumnName("conversation_key").HasMaxLength(512).IsRequired();
        e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(512);
        e.Property(x => x.ProviderAccountId).HasColumnName("provider_account_id").HasMaxLength(256);
        e.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(8).IsRequired();
        e.Property(x => x.Text).HasColumnName("text").IsRequired();
        e.Property(x => x.Basis).HasColumnName("basis").HasMaxLength(16).IsRequired();
        e.Property(x => x.TemplateId).HasColumnName("template_id").HasMaxLength(64);
        e.Property(x => x.Decoded).HasColumnName("decoded").HasColumnType("jsonb");
        e.Property(x => x.EndpointName).HasColumnName("endpoint_name").HasMaxLength(64);
        e.Property(x => x.PromptTokens).HasColumnName("prompt_tokens");
        e.Property(x => x.CompletionTokens).HasColumnName("completion_tokens");
        e.Property(x => x.TraceId).HasColumnName("trace_id").HasMaxLength(64);
        e.ToTable(t => t.HasCheckConstraint("ai_turns_direction_chk", "direction IN ('in','out')"));
        e.ToTable(t => t.HasCheckConstraint("ai_turns_basis_chk", "basis IN ('slash','model','fallback')"));
        e.HasIndex(x => new { x.ConversationKey, x.OccurredAt }).HasDatabaseName("ix_ai_turns_conversation_key_occurred_at");
    }
}

/// <summary>facilitator.actor_preferences — per-actor AI verbosity.</summary>
public sealed class ActorPreferenceEntityConfiguration : IEntityTypeConfiguration<ActorPreferenceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ActorPreferenceEntity> e)
    {
        e.ToTable("actor_preferences");
        e.HasKey(x => x.Actor);
        e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(512).ValueGeneratedNever();
        e.Property(x => x.Verbosity).HasColumnName("verbosity").HasMaxLength(16).IsRequired();
        e.Property(x => x.Extras).HasColumnName("extras").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.ToTable(t => t.HasCheckConstraint("actor_preferences_verbosity_chk", "verbosity IN ('terse','balanced','proactive')"));
    }
}
