using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_MessagingReportsThemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_channels",
                columns: table => new
                {
                    EmailChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    PasswordEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: true),
                    DefaultRecipients = table.Column<string>(type: "text", nullable: true),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_channels", x => x.EmailChannelId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_channels",
                columns: table => new
                {
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    WebhookUrlEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    TargetHost = table.Column<string>(type: "text", nullable: true),
                    HeadersJson = table.Column<string>(type: "text", nullable: true),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastCheckError = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_channels", x => x.MessagingChannelId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_deliveries",
                columns: table => new
                {
                    MessagingDeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Preview = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ElapsedMs = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_deliveries", x => x.MessagingDeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "report_artifacts",
                columns: table => new
                {
                    ReportArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_artifacts", x => x.ReportArtifactId);
                });

            migrationBuilder.CreateTable(
                name: "themes",
                columns: table => new
                {
                    ThemeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ColorsJson = table.Column<string>(type: "text", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_themes", x => x.ThemeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_Name",
                table: "email_channels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_Slug",
                table: "email_channels",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_Name",
                table: "messaging_channels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_Slug",
                table: "messaging_channels",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_MessagingChannelId_SentAt",
                table: "messaging_deliveries",
                columns: new[] { "MessagingChannelId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CreatedAt",
                table: "report_artifacts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_ExpiresAt",
                table: "report_artifacts",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_themes_IsShared_OwnerUserId",
                table: "themes",
                columns: new[] { "IsShared", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_channels");

            migrationBuilder.DropTable(
                name: "messaging_channels");

            migrationBuilder.DropTable(
                name: "messaging_deliveries");

            migrationBuilder.DropTable(
                name: "report_artifacts");

            migrationBuilder.DropTable(
                name: "themes");
        }
    }
}
