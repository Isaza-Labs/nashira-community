using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_AiApiSpecActiveUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs",
                column: "Api",
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs",
                column: "Api",
                unique: true);
        }
    }
}
