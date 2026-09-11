using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_ThemeStyleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "{}" rather than the scaffolder's "": every theme that already
            // exists carries no style overrides, and an empty object says that
            // directly instead of leaning on readers to treat "" as empty.
            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "themes",
                type: "text",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "themes");
        }
    }
}
