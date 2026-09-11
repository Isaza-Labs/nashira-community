using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_IntegrationsAndMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_actions",
                columns: table => new
                {
                    IntegrationActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    PathParamsJson = table.Column<string>(type: "text", nullable: true),
                    QueryParamsJson = table.Column<string>(type: "text", nullable: true),
                    RequestBodyJson = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    ReadOnly = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_actions", x => x.IntegrationActionId);
                });

            migrationBuilder.CreateTable(
                name: "integrations",
                columns: table => new
                {
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BaseUrl = table.Column<string>(type: "text", nullable: false),
                    AuthConfig = table.Column<string>(type: "text", nullable: true),
                    HeadersJson = table.Column<string>(type: "text", nullable: true),
                    VerifySsl = table.Column<bool>(type: "boolean", nullable: false),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    HealthCheckPath = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("PK_integrations", x => x.IntegrationId);
                });

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Transport = table.Column<string>(type: "text", nullable: false),
                    AuthType = table.Column<string>(type: "text", nullable: false),
                    AuthConfigEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    HeadersJson = table.Column<string>(type: "text", nullable: true),
                    TlsSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastCheckError = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ToolsSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ToolCount = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_servers", x => x.McpServerId);
                });

            migrationBuilder.CreateTable(
                name: "mcp_tools",
                columns: table => new
                {
                    McpToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputSchemaJson = table.Column<string>(type: "text", nullable: false),
                    DisappearedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_tools", x => x.McpToolId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_actions_IntegrationId",
                table: "integration_actions",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_actions_IntegrationId_OperationId",
                table: "integration_actions",
                columns: new[] { "IntegrationId", "OperationId" },
                unique: true,
                filter: "\"OperationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Name",
                table: "integrations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Slug",
                table: "integrations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Type",
                table: "integrations",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_McpServerId",
                table: "mcp_tools",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_McpServerId_Name",
                table: "mcp_tools",
                columns: new[] { "McpServerId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_actions");

            migrationBuilder.DropTable(
                name: "integrations");

            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropTable(
                name: "mcp_tools");
        }
    }
}
