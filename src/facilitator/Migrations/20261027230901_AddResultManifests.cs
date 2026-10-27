using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddResultManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.CreateTable(
                name: "result_manifests",
                schema: "facilitator",
                columns: table => new
                {
                    manifest_id = table.Column<string>(type: "text", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    revision_of = table.Column<string>(type: "text", nullable: true),
                    digest = table.Column<string>(type: "text", nullable: false),
                    structured_digest = table.Column<string>(type: "text", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_artifact_ids = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    result_state = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_result_manifests", x => x.manifest_id);
                    table.CheckConstraint("result_manifests_digest_check", "digest ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("result_manifests_result_state_check", "result_state IN ('missing', 'available', 'validated', 'rejected', 'superseded')");
                    table.CheckConstraint("result_manifests_state_check", "state IN ('recorded', 'superseded')");
                    table.ForeignKey(
                        name: "result_manifests_revision_of_fkey",
                        column: x => x.revision_of,
                        principalSchema: "facilitator",
                        principalTable: "result_manifests",
                        principalColumn: "manifest_id");
                    table.ForeignKey(
                        name: "result_manifests_work_id_fkey",
                        column: x => x.work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded')");

            migrationBuilder.CreateIndex(
                name: "ix_result_manifests_assignment_id",
                schema: "facilitator",
                table: "result_manifests",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_result_manifests_revision_of",
                schema: "facilitator",
                table: "result_manifests",
                column: "revision_of");

            migrationBuilder.CreateIndex(
                name: "ix_result_manifests_work_id",
                schema: "facilitator",
                table: "result_manifests",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "result_manifests_assignment_id_revision_key",
                schema: "facilitator",
                table: "result_manifests",
                columns: new[] { "assignment_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "result_manifests",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc')");
        }
    }
}
