using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_Snippets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "snippets",
                columns: table => new
                {
                    SnippetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    OutputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ScriptLanguage = table.Column<string>(type: "text", nullable: true),
                    TargetMode = table.Column<string>(type: "text", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Idempotency = table.Column<string>(type: "text", nullable: true),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    LogicDiagramMermaid = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snippets", x => x.SnippetId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Name",
                table: "snippets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Slug",
                table: "snippets",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Type",
                table: "snippets",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "snippets");
        }
    }
}
