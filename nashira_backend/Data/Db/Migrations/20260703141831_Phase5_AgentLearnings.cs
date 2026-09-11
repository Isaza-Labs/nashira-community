using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase5_AgentLearnings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_learnings",
                columns: table => new
                {
                    AgentLearningId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErrorPattern = table.Column<string>(type: "text", nullable: false),
                    ErrorCategory = table.Column<string>(type: "text", nullable: false),
                    ServiceType = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: false),
                    FixStrategy = table.Column<string>(type: "text", nullable: false),
                    FixParamsJson = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    SuccessCount = table.Column<long>(type: "bigint", nullable: false),
                    FailureCount = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_learnings", x => x.AgentLearningId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId",
                table: "agent_learnings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId_Category_Confidence",
                table: "agent_learnings",
                columns: new[] { "CompanyId", "Category", "Confidence" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId_ToolName",
                table: "agent_learnings",
                columns: new[] { "CompanyId", "ToolName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_learnings");
        }
    }
}
