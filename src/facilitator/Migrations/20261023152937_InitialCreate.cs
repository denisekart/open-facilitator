using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "facilitator");

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "facilitator",
                columns: table => new
                {
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_digest = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    nonce = table.Column<string>(type: "text", nullable: false),
                    eligible_actors = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approvals", x => x.approval_id);
                    table.CheckConstraint("approvals_state_check", "state IN ('pending', 'approved', 'denied', 'expired', 'revoked', 'consumed')");
                });

            migrationBuilder.CreateTable(
                name: "communication_bindings",
                schema: "facilitator",
                columns: table => new
                {
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    root_message_id = table.Column<string>(type: "text", nullable: false),
                    parent_message_id = table.Column<string>(type: "text", nullable: true),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communication_bindings", x => x.binding_id);
                });

            migrationBuilder.CreateTable(
                name: "dead_letters",
                schema: "facilitator",
                columns: table => new
                {
                    dead_letter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letters", x => x.dead_letter_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "facilitator",
                columns: table => new
                {
                    inbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_source = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    command_id = table.Column<string>(type: "text", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: true),
                    provider_delivery_id = table.Column<string>(type: "text", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_sha256 = table.Column<string>(type: "text", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    leased_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => x.inbox_id);
                    table.CheckConstraint("inbox_state_check", "state IN ('pending', 'processing', 'processed', 'failed', 'duplicate')");
                });

            migrationBuilder.CreateTable(
                name: "link_sessions",
                schema: "facilitator",
                columns: table => new
                {
                    logical_endpoint_id = table.Column<string>(type: "text", nullable: false),
                    connection_instance_id = table.Column<string>(type: "text", nullable: false),
                    replica_id = table.Column<string>(type: "text", nullable: false),
                    session_epoch = table.Column<long>(type: "bigint", nullable: false),
                    lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    capabilities_digest = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_sessions", x => x.logical_endpoint_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "facilitator",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_endpoint_id = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    command_id = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    leased_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.outbox_id);
                    table.CheckConstraint("outbox_aggregate_type_check", "aggregate_type IN ('work', 'assignment')");
                    table.CheckConstraint("outbox_status_check", "status IN ('pending', 'leased', 'delivered', 'dead_letter')");
                });

            migrationBuilder.CreateTable(
                name: "provider_events",
                schema: "facilitator",
                columns: table => new
                {
                    provider_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_delivery_id = table.Column<string>(type: "text", nullable: true),
                    body_sha256 = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    verified_reference = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_events", x => x.provider_event_id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "facilitator",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.team_id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "facilitator",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.project_id);
                    table.ForeignKey(
                        name: "projects_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "works",
                schema: "facilitator",
                columns: table => new
                {
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    objective = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_works", x => x.work_id);
                    table.CheckConstraint("works_state_check", "state IN ('open', 'planning', 'active', 'review', 'completed', 'blocked', 'cancelled', 'failed')");
                    table.ForeignKey(
                        name: "works_project_id_fkey",
                        column: x => x.project_id,
                        principalSchema: "facilitator",
                        principalTable: "projects",
                        principalColumn: "project_id");
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                schema: "facilitator",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_execution_generation = table.Column<int>(type: "integer", nullable: false),
                    expected_context_version = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.assignment_id);
                    table.CheckConstraint("assignments_status_check", "status IN ('proposed', 'routed', 'accepted', 'running', 'input_required', 'completed', 'failed', 'cancelled', 'expired')");
                    table.ForeignKey(
                        name: "assignments_work_id_fkey",
                        column: x => x.work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_events",
                schema: "facilitator",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_version = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_events", x => x.event_id);
                    table.CheckConstraint("work_events_kind_check", "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition')");
                    table.ForeignKey(
                        name: "work_events_work_id_fkey",
                        column: x => x.work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "execution_attempts",
                schema: "facilitator",
                columns: table => new
                {
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_attempts", x => x.execution_id);
                    table.CheckConstraint("execution_attempts_status_check", "status IN ('starting', 'ready', 'working', 'stopping', 'stopped', 'failed', 'lost')");
                    table.ForeignKey(
                        name: "execution_attempts_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalSchema: "facilitator",
                        principalTable: "assignments",
                        principalColumn: "assignment_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_work_id",
                schema: "facilitator",
                table: "assignments",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_execution_attempts_assignment_id",
                schema: "facilitator",
                table: "execution_attempts",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "inbox_command_id_key",
                schema: "facilitator",
                table: "inbox",
                column: "command_id",
                unique: true,
                filter: "command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "inbox_due",
                schema: "facilitator",
                table: "inbox",
                columns: new[] { "available_at", "leased_until" },
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "inbox_event_source_event_id_key",
                schema: "facilitator",
                table: "inbox",
                columns: new[] { "event_source", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "inbox_provider_delivery_unique",
                schema: "facilitator",
                table: "inbox",
                columns: new[] { "provider", "provider_delivery_id" },
                unique: true,
                filter: "provider IS NOT NULL AND provider_delivery_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "outbox_command_id_key",
                schema: "facilitator",
                table: "outbox",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "outbox_due",
                schema: "facilitator",
                table: "outbox",
                columns: new[] { "available_at", "leased_until" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "outbox_leased",
                schema: "facilitator",
                table: "outbox",
                column: "leased_until",
                filter: "status = 'leased'");

            migrationBuilder.CreateIndex(
                name: "outbox_work_id_idx",
                schema: "facilitator",
                table: "outbox",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_team_id",
                schema: "facilitator",
                table: "projects",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "provider_events_provider_delivery_unique",
                schema: "facilitator",
                table: "provider_events",
                columns: new[] { "provider", "provider_delivery_id" },
                unique: true,
                filter: "provider IS NOT NULL AND provider_delivery_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "teams_slug_key",
                schema: "facilitator",
                table: "teams",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "work_events_work_id_created_at_idx",
                schema: "facilitator",
                table: "work_events",
                columns: new[] { "work_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_works_project_id",
                schema: "facilitator",
                table: "works",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "communication_bindings",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "dead_letters",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "execution_attempts",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "link_sessions",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "provider_events",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "work_events",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "assignments",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "works",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "facilitator");
        }
    }
}
