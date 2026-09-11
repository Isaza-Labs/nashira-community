using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_WorkflowTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_triggers",
                columns: table => new
                {
                    WorkflowTriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    Timezone = table.Column<string>(type: "text", nullable: false),
                    Route = table.Column<string>(type: "text", nullable: true),
                    EncryptedSecret = table.Column<byte[]>(type: "bytea", nullable: true),
                    AllowUnsigned = table.Column<bool>(type: "boolean", nullable: false),
                    AllowTargetOverride = table.Column<bool>(type: "boolean", nullable: false),
                    TargetDevicesJson = table.Column<string>(type: "text", nullable: false),
                    InputDefaultsJson = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    FireCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_triggers", x => x.WorkflowTriggerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_Enabled_Type_NextRunAt",
                table: "workflow_triggers",
                columns: new[] { "Enabled", "Type", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_Route",
                table: "workflow_triggers",
                column: "Route",
                unique: true,
                filter: "\"Route\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_WorkflowId",
                table: "workflow_triggers",
                column: "WorkflowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_triggers");
        }
    }
}
