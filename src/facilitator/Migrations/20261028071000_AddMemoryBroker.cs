using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.CreateTable(
                name: "memory_scopes",
                schema: "facilitator",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    team_slug = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    canonical_selector = table.Column<string>(type: "text", nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_scopes", x => x.scope_id);
                    table.CheckConstraint("memory_scopes_kind_check", "kind IN ('work', 'project', 'team', 'private')");
                });

            migrationBuilder.CreateTable(
                name: "memory_usage",
                schema: "facilitator",
                columns: table => new
                {
                    usage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    justification = table.Column<string>(type: "text", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_usage", x => x.usage_id);
                });

            migrationBuilder.CreateTable(
                name: "memory_entries",
                schema: "facilitator",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_slug = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    content_summary = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    provenance = table.Column<string>(type: "jsonb", nullable: false),
                    observed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<string>(type: "text", nullable: false),
                    policy_label = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    supersedes_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contradicts_entry_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_entries", x => x.entry_id);
                    table.CheckConstraint("memory_entries_classification_check", "classification IN ('internal', 'public')");
                    table.CheckConstraint("memory_entries_content_hash_check", "content_hash ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("memory_entries_policy_label_check", "policy_label IN ('private', 'team-only', 'facilitator-visible', 'evidence-only')");
                    table.CheckConstraint("memory_entries_status_check", "status IN ('candidate', 'active', 'superseded', 'tombstoned')");
                    table.CheckConstraint("memory_entries_type_check", "type IN ('observed_fact', 'experience', 'decision', 'lesson_candidate', 'lesson_verified', 'hypothesis', 'preference')");
                    table.ForeignKey(
                        name: "memory_entries_scope_id_fkey",
                        column: x => x.scope_id,
                        principalSchema: "facilitator",
                        principalTable: "memory_scopes",
                        principalColumn: "scope_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The FTS surface stays out of the EF model on purpose:
            // the broker never reads or writes `search`; Postgres recomputes
            // the stored generated column when tombstoning empties `content`.
            migrationBuilder.Sql(
                "ALTER TABLE facilitator.memory_entries ADD COLUMN search tsvector GENERATED ALWAYS AS (to_tsvector('english'::regconfig, content)) STORED;");
            migrationBuilder.Sql(
                "CREATE INDEX ix_memory_entries_search ON facilitator.memory_entries USING gin (search);");
            migrationBuilder.CreateTable(
                name: "memory_shares",
                schema: "facilitator",
                columns: table => new
                {
                    share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_team_slug = table.Column<string>(type: "text", nullable: false),
                    granted_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_shares", x => x.share_id);
                    table.ForeignKey(
                        name: "memory_shares_entry_id_fkey",
                        column: x => x.entry_id,
                        principalSchema: "facilitator",
                        principalTable: "memory_entries",
                        principalColumn: "entry_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted')");

            migrationBuilder.CreateIndex(
                name: "ix_memory_entries_expires",
                schema: "facilitator",
                table: "memory_entries",
                column: "expires_at",
                filter: "status <> 'tombstoned'");

            migrationBuilder.CreateIndex(
                name: "ix_memory_entries_scope",
                schema: "facilitator",
                table: "memory_entries",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_memory_entries_team",
                schema: "facilitator",
                table: "memory_entries",
                column: "team_slug");

            migrationBuilder.CreateIndex(
                name: "ix_memory_scopes_selector",
                schema: "facilitator",
                table: "memory_scopes",
                column: "canonical_selector",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memory_scopes_team",
                schema: "facilitator",
                table: "memory_scopes",
                column: "team_slug");

            migrationBuilder.CreateIndex(
                name: "ix_memory_shares_pair",
                schema: "facilitator",
                table: "memory_shares",
                columns: new[] { "entry_id", "grantee_team_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memory_usage_entry",
                schema: "facilitator",
                table: "memory_usage",
                column: "entry_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_shares",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "memory_usage",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "memory_entries",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "memory_scopes",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded')");
        }
    }
}
