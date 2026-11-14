using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowPausedStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "workflow_instances_status_check",
                schema: "facilitator",
                table: "workflow_instances");

            migrationBuilder.AddCheckConstraint(
                name: "workflow_instances_status_check",
                schema: "facilitator",
                table: "workflow_instances",
                sql: "status IN ('running', 'waiting', 'paused', 'completed', 'failed', 'cancelled', 'shadowed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "workflow_instances_status_check",
                schema: "facilitator",
                table: "workflow_instances");

            migrationBuilder.AddCheckConstraint(
                name: "workflow_instances_status_check",
                schema: "facilitator",
                table: "workflow_instances",
                sql: "status IN ('running', 'waiting', 'completed', 'failed', 'cancelled', 'shadowed')");
        }
    }
}
