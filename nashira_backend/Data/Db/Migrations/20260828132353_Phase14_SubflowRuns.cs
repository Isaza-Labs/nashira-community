using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <summary>
    /// The two links a subflow run needs (execution/SPEC.md §5): the child run points at
    /// the parent it was started from, and the parent step points at the child run it
    /// started. Both nullable — every run before this one is a root, and every step that
    /// is not a <c>subflow</c> node still has no child.
    /// </summary>
    public partial class Phase14_SubflowRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentRunId",
                table: "workflow_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChildRunId",
                table: "step_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_ParentRunId",
                table: "workflow_runs",
                column: "ParentRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_ParentRunId",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "ChildRunId",
                table: "step_runs");
        }
    }
}
