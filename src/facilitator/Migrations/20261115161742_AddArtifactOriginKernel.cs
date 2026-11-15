using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations;

/// <inheritdoc />
public partial class AddArtifactOriginKernel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The context plane mints kernel-owned artifacts (context.dispatch,
        // context.history): the origin law gains the `kernel` value.
        migrationBuilder.DropCheckConstraint(
            name: "artifacts_origin_check",
            schema: "facilitator",
            table: "artifacts");

        migrationBuilder.AddCheckConstraint(
            name: "artifacts_origin_check",
            schema: "facilitator",
            table: "artifacts",
            sql: "origin IN ('link', 'rest', 'external', 'kernel')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "artifacts_origin_check",
            schema: "facilitator",
            table: "artifacts");

        migrationBuilder.AddCheckConstraint(
            name: "artifacts_origin_check",
            schema: "facilitator",
            table: "artifacts",
            sql: "origin IN ('link', 'rest', 'external')");
    }
}
