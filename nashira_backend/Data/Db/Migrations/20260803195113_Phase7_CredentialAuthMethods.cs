using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase7_CredentialAuthMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHeader",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedClientSecret",
                table: "credentials",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedToken",
                table: "credentials",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenUrl",
                table: "credentials",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeyHeader",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "EncryptedClientSecret",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "EncryptedToken",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "TokenUrl",
                table: "credentials");
        }
    }
}
