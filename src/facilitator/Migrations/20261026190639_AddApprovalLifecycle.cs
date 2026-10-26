using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddColumn<string>(
                name: "actor_evidence_ref",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "comment",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                schema: "facilitator",
                table: "approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "digest_verification",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: false,
                defaultValue: "digest-unverified");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided')");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_work_id",
                schema: "facilitator",
                table: "approvals",
                column: "work_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropIndex(
                name: "ix_approvals_work_id",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "actor_evidence_ref",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "comment",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "decided_by",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "digest_verification",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition')");
        }
    }
}
