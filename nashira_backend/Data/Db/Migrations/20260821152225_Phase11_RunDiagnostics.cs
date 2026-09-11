using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_RunDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "workflow_runs",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Existing rows would otherwise read as an empty trigger, which on screen
            // is a blank column nobody can interpret. The distinction the old schema
            // did record is whether a person was signed in when it ran, so say that
            // much and no more: everything else was started by something automated,
            // and this migration cannot know which.
            migrationBuilder.Sql(@"
                UPDATE workflow_runs
                SET ""Trigger"" = CASE
                    WHEN ""TriggeredByUserId"" IS NOT NULL THEN 'manual'
                    ELSE 'schedule'
                END
                WHERE ""Trigger"" = '';");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "step_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "step_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "step_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputJson",
                table: "step_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Logs",
                table: "step_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "step_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "InputJson",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "Logs",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "step_runs");
        }
    }
}
