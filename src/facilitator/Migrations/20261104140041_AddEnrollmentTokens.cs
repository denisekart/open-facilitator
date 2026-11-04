using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrollmentTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrollment_tokens",
                schema: "facilitator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    team = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed_endpoint_id = table.Column<string>(type: "text", nullable: true),
                    link_enrollment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrollment_tokens", x => x.id);
                    table.CheckConstraint("enrollment_tokens_role_check", "role IN ('gateway', 'child')");
                });

            migrationBuilder.CreateIndex(
                name: "enrollment_tokens_token_hash_key",
                schema: "facilitator",
                table: "enrollment_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrollment_tokens",
                schema: "facilitator");
        }
    }
}
