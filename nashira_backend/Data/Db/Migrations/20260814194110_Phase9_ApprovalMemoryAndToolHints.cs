using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_ApprovalMemoryAndToolHints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReadOnlyHint",
                table: "mcp_tools",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TrustToolHints",
                table: "mcp_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedToolsJson",
                table: "ai_conversations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadOnlyHint",
                table: "mcp_tools");

            migrationBuilder.DropColumn(
                name: "TrustToolHints",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "ApprovedToolsJson",
                table: "ai_conversations");
        }
    }
}
