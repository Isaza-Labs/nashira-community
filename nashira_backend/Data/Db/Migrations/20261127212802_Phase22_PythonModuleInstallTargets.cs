using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase22_PythonModuleInstallTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InstallInSystem",
                table: "allowed_python_modules",
                type: "boolean",
                nullable: false,
                // Every row that exists today installs into the system Python — the
                // only target there was. Edited from the generated `false` on purpose.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "python_module_environments",
                columns: table => new
                {
                    AllowedPythonModuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PythonEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_python_module_environments", x => new { x.AllowedPythonModuleId, x.PythonEnvironmentId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_python_module_environments_PythonEnvironmentId",
                table: "python_module_environments",
                column: "PythonEnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "python_module_environments");

            migrationBuilder.DropColumn(
                name: "InstallInSystem",
                table: "allowed_python_modules");
        }
    }
}
