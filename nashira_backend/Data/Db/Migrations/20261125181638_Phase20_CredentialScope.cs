using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase20_CredentialScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedHosts",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OperatorAssignable",
                table: "credentials",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedHosts",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "OperatorAssignable",
                table: "credentials");
        }
    }
}
