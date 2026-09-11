using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_JobsAndHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF scaffolds `false` for every bool, but the executor this column
            // replaces HARDCODED allowPrivate: true — so true is the behaviour
            // every existing spec was configured under. Backfilling false would
            // silently break every on-prem spec on upgrade; the change is that an
            // admin can now opt a spec OUT.
            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateNetwork",
                table: "ai_api_specs",
                type: "boolean",
                nullable: false,
                defaultValueSql: "true");

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ClaimedBy = table.Column<string>(type: "text", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowTriggerId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryKey = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.JobId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_CreatedAt",
                table: "jobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_WorkflowTriggerId_DeliveryKey",
                table: "jobs",
                columns: new[] { "WorkflowTriggerId", "DeliveryKey" },
                unique: true,
                filter: "\"DeliveryKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropColumn(
                name: "AllowPrivateNetwork",
                table: "ai_api_specs");
        }
    }
}
