using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddAiDecoderChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "actor_preferences",
                schema: "facilitator",
                columns: table => new
                {
                    actor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    verbosity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    extras = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actor_preferences", x => x.actor);
                    table.CheckConstraint("actor_preferences_verbosity_chk", "verbosity IN ('terse','balanced','proactive')");
                });

            migrationBuilder.CreateTable(
                name: "ai_turns",
                schema: "facilitator",
                columns: table => new
                {
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    conversation_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    provider_account_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    decoded = table.Column<string>(type: "jsonb", nullable: true),
                    endpoint_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_turns", x => x.turn_id);
                    table.CheckConstraint("ai_turns_basis_chk", "basis IN ('slash','model','fallback')");
                    table.CheckConstraint("ai_turns_direction_chk", "direction IN ('in','out')");
                });

            migrationBuilder.CreateTable(
                name: "chat_sessions",
                schema: "facilitator",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                schema: "facilitator",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.message_id);
                    table.CheckConstraint("chat_messages_basis_chk", "basis IN ('model','fallback','slash')");
                    table.CheckConstraint("chat_messages_role_chk", "role IN ('user','assistant')");
                    table.ForeignKey(
                        name: "chat_messages_session_id_fkey",
                        column: x => x.session_id,
                        principalSchema: "facilitator",
                        principalTable: "chat_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_actions",
                schema: "facilitator",
                columns: table => new
                {
                    action_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    executed_by = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_actions", x => x.action_id);
                    table.CheckConstraint("chat_actions_status_chk", "status IN ('pending','executed','failed','cancelled')");
                    table.ForeignKey(
                        name: "chat_actions_message_id_fkey",
                        column: x => x.message_id,
                        principalSchema: "facilitator",
                        principalTable: "chat_messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "chat_actions_session_id_fkey",
                        column: x => x.session_id,
                        principalSchema: "facilitator",
                        principalTable: "chat_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_turns_conversation_key_occurred_at",
                schema: "facilitator",
                table: "ai_turns",
                columns: new[] { "conversation_key", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_actions_message_id",
                schema: "facilitator",
                table: "chat_actions",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_actions_session_id",
                schema: "facilitator",
                table: "chat_actions",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_actions_status",
                schema: "facilitator",
                table: "chat_actions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_session_id_created_at",
                schema: "facilitator",
                table: "chat_messages",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_sessions_last_message_at",
                schema: "facilitator",
                table: "chat_sessions",
                column: "last_message_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actor_preferences",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "ai_turns",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "chat_actions",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "chat_messages",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "chat_sessions",
                schema: "facilitator");
        }
    }
}
