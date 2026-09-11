using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase5_LoaderSkillsValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_prompt_skills",
                columns: table => new
                {
                    AiPromptSkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_prompt_skills", x => x.AiPromptSkillId);
                });

            migrationBuilder.CreateTable(
                name: "validation_records",
                columns: table => new
                {
                    ValidationRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TargetName = table.Column<string>(type: "text", nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_records", x => x.ValidationRecordId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_records_CompanyId",
                table: "validation_records",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_validation_records_CompanyId_Kind",
                table: "validation_records",
                columns: new[] { "CompanyId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_prompt_skills");

            migrationBuilder.DropTable(
                name: "validation_records");
        }
    }
}
