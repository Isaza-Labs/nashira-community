using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_PythonPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "allowed_python_modules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstalledVersion",
                table: "allowed_python_modules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PipSpec",
                table: "allowed_python_modules",
                type: "text",
                nullable: true);

            // Every row that exists today is a standard-library allowlist entry, and
            // the standard library is already installed. Defaulting to stdlib/ready is
            // what keeps those rows working: an empty status would match nothing, and
            // every existing snippet would lose its imports on upgrade.
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "allowed_python_modules",
                type: "text",
                nullable: false,
                defaultValue: "stdlib");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "allowed_python_modules",
                type: "text",
                nullable: false,
                defaultValue: "ready");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "allowed_python_modules");

            migrationBuilder.DropColumn(
                name: "InstalledVersion",
                table: "allowed_python_modules");

            migrationBuilder.DropColumn(
                name: "PipSpec",
                table: "allowed_python_modules");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "allowed_python_modules");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "allowed_python_modules");
        }
    }
}
