using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provisioning_operations",
                schema: "facilitator",
                columns: table => new
                {
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    workload_id = table.Column<string>(type: "text", nullable: false),
                    team = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provisioning_operations", x => x.operation_id);
                    table.CheckConstraint("provisioning_operations_operation_check", "operation IN ('start', 'stop')");
                    table.CheckConstraint("provisioning_operations_state_check", "state IN ('requested', 'running', 'stopped', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "provisioning_operations_workload_idx",
                schema: "facilitator",
                table: "provisioning_operations",
                column: "workload_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provisioning_operations",
                schema: "facilitator");
        }
    }
}
