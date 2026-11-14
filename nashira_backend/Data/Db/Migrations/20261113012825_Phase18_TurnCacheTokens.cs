using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase18_TurnCacheTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TokensCacheRead",
                table: "agent_turns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensCacheWrite",
                table: "agent_turns",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokensCacheRead",
                table: "agent_turns");

            migrationBuilder.DropColumn(
                name: "TokensCacheWrite",
                table: "agent_turns");
        }
    }
}
