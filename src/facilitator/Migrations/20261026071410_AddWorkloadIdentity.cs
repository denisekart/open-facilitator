using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkloadIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "accepts_delegated_claims",
                schema: "facilitator",
                table: "link_enrollments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "facilitator",
                table: "link_enrollments",
                type: "text",
                nullable: false,
                defaultValue: "gateway");

            migrationBuilder.AddColumn<string>(
                name: "workload_issuer",
                schema: "facilitator",
                table: "link_enrollments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "workload_subject",
                schema: "facilitator",
                table: "link_enrollments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "evidence",
                schema: "facilitator",
                columns: table => new
                {
                    evidence_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    claims = table.Column<string>(type: "jsonb", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence", x => x.evidence_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "link_enrollments_role_check",
                schema: "facilitator",
                table: "link_enrollments",
                sql: "role IN ('gateway', 'child')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "link_enrollments_role_check",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "accepts_delegated_claims",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "workload_issuer",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "workload_subject",
                schema: "facilitator",
                table: "link_enrollments");
        }
    }
}
