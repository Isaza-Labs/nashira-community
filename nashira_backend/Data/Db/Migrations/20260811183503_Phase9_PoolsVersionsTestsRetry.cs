using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_PoolsVersionsTestsRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RetryPolicyJson",
                table: "snippets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_pools",
                columns: table => new
                {
                    DevicePoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    FilterRulesJson = table.Column<string>(type: "text", nullable: true),
                    StaticMembersJson = table.Column<string>(type: "text", nullable: false),
                    AllowDraft = table.Column<bool>(type: "boolean", nullable: false),
                    AllowQa = table.Column<bool>(type: "boolean", nullable: false),
                    AllowProduction = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_pools", x => x.DevicePoolId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_acceptance_tests",
                columns: table => new
                {
                    WorkflowAcceptanceTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    TargetDevicesJson = table.Column<string>(type: "text", nullable: false),
                    AssertionsJson = table.Column<string>(type: "text", nullable: false),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailuresJson = table.Column<string>(type: "text", nullable: true),
                    LastSchemaHash = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_acceptance_tests", x => x.WorkflowAcceptanceTestId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                columns: table => new
                {
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    NodesJson = table.Column<string>(type: "text", nullable: false),
                    EdgesJson = table.Column<string>(type: "text", nullable: false),
                    InputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    SchemaHash = table.Column<string>(type: "text", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    ChangeSummary = table.Column<string>(type: "text", nullable: false),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PromotedBy = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SimulationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.WorkflowVersionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_Name",
                table: "device_pools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_Slug",
                table: "device_pools",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_WorkflowId",
                table: "workflow_acceptance_tests",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowId_Version",
                table: "workflow_versions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_pools");

            migrationBuilder.DropTable(
                name: "workflow_acceptance_tests");

            migrationBuilder.DropTable(
                name: "workflow_versions");

            migrationBuilder.DropColumn(
                name: "RetryPolicyJson",
                table: "snippets");
        }
    }
}
