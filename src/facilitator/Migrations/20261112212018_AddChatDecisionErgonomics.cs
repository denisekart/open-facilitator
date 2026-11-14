using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddChatDecisionErgonomics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chat_actions_status_chk",
                schema: "facilitator",
                table: "chat_actions");

            migrationBuilder.AddColumn<string>(
                name: "reads",
                schema: "facilitator",
                table: "chat_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                schema: "facilitator",
                table: "chat_actions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "facilitator",
                table: "chat_actions",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chat_actions_status_chk",
                schema: "facilitator",
                table: "chat_actions",
                sql: "status IN ('pending','executed','failed','cancelled','expired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chat_actions_status_chk",
                schema: "facilitator",
                table: "chat_actions");

            migrationBuilder.DropColumn(
                name: "reads",
                schema: "facilitator",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "expires_at",
                schema: "facilitator",
                table: "chat_actions");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "facilitator",
                table: "chat_actions");

            migrationBuilder.AddCheckConstraint(
                name: "chat_actions_status_chk",
                schema: "facilitator",
                table: "chat_actions",
                sql: "status IN ('pending','executed','failed','cancelled')");
        }
    }
}
