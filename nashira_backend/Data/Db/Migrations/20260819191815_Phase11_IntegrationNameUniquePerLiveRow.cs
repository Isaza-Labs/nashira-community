using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_IntegrationNameUniquePerLiveRow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_integrations_Name",
                table: "integrations");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Name",
                table: "integrations",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_integrations_Name",
                table: "integrations");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Name",
                table: "integrations",
                column: "Name",
                unique: true);
        }
    }
}
