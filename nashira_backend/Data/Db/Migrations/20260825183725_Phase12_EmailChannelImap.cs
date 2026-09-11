using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase12_EmailChannelImap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImapHost",
                table: "email_channels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ImapPasswordEncrypted",
                table: "email_channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImapPort",
                table: "email_channels",
                type: "integer",
                nullable: false,
                defaultValue: 993);

            migrationBuilder.AddColumn<string>(
                name: "ImapSecurity",
                table: "email_channels",
                type: "text",
                nullable: false,
                defaultValue: "ssl");

            migrationBuilder.AddColumn<string>(
                name: "ImapUsername",
                table: "email_channels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImapHost",
                table: "email_channels");

            migrationBuilder.DropColumn(
                name: "ImapPasswordEncrypted",
                table: "email_channels");

            migrationBuilder.DropColumn(
                name: "ImapPort",
                table: "email_channels");

            migrationBuilder.DropColumn(
                name: "ImapSecurity",
                table: "email_channels");

            migrationBuilder.DropColumn(
                name: "ImapUsername",
                table: "email_channels");
        }
    }
}
