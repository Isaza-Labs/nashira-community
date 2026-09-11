using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase17_NavigationPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "navigation_permissions",
                columns: table => new
                {
                    NavigationPermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Visible = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_navigation_permissions", x => x.NavigationPermissionId);
                    table.CheckConstraint("CK_navigation_permissions_scope", "(\"Role\" IS NOT NULL AND \"UserId\" IS NULL) OR (\"Role\" IS NULL AND \"UserId\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_navigation_permissions_Role_PageKey",
                table: "navigation_permissions",
                columns: new[] { "Role", "PageKey" },
                unique: true,
                filter: "\"Role\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_navigation_permissions_UserId_PageKey",
                table: "navigation_permissions",
                columns: new[] { "UserId", "PageKey" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "navigation_permissions");
        }
    }
}
