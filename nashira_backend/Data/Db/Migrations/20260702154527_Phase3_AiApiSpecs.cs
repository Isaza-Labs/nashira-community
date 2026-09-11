using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_AiApiSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_api_specs",
                columns: table => new
                {
                    AiApiSpecId = table.Column<Guid>(type: "uuid", nullable: false),
                    Api = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    OperationCount = table.Column<int>(type: "integer", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    AuthType = table.Column<string>(type: "text", nullable: false),
                    AuthConfig = table.Column<string>(type: "text", nullable: true),
                    VerifySsl = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_api_specs", x => x.AiApiSpecId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "Api" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_api_specs");
        }
    }
}
