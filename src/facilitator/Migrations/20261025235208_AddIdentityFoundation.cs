using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit",
                schema: "facilitator",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    resource = table.Column<string>(type: "text", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    policy_version = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit", x => x.audit_id);
                    table.CheckConstraint("audit_decision_check", "decision IN ('allow', 'deny')");
                });

            migrationBuilder.CreateTable(
                name: "identity_roles",
                schema: "facilitator",
                columns: table => new
                {
                    role_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_roles", x => x.role_name);
                });

            migrationBuilder.CreateIndex(
                name: "audit_actor_occurred_at_idx",
                schema: "facilitator",
                table: "audit",
                columns: new[] { "actor", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "audit_occurred_at_idx",
                schema: "facilitator",
                table: "audit",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "identity_roles",
                schema: "facilitator");
        }
    }
}
