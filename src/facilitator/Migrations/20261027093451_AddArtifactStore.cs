using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "facilitator",
                columns: table => new
                {
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    digest = table.Column<string>(type: "text", nullable: false),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    verification = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    team_slug = table.Column<string>(type: "text", nullable: true),
                    requested_by_endpoint_id = table.Column<string>(type: "text", nullable: true),
                    command_id = table.Column<string>(type: "text", nullable: true),
                    wire_artifact_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stored_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retention_policy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.artifact_id);
                    table.CheckConstraint("artifacts_origin_check", "origin IN ('link', 'rest', 'external')");
                    table.CheckConstraint("artifacts_state_check", "state IN ('pending', 'stored', 'rejected', 'deleted')");
                    table.CheckConstraint("artifacts_verification_check", "verification IN ('verified', 'unverified')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_command_id",
                schema: "facilitator",
                table: "artifacts",
                column: "command_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_digest",
                schema: "facilitator",
                table: "artifacts",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_wire_artifact_id",
                schema: "facilitator",
                table: "artifacts",
                column: "wire_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_work_id",
                schema: "facilitator",
                table: "artifacts",
                column: "work_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "facilitator");
        }
    }
}
