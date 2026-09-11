using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_SoftDeleteAwareUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_Intent_Platform",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_snippets_Name",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_profiles_Name",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_policies_Name",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "IX_messaging_channels_Name",
                table: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_articles_Slug",
                table: "knowledge_articles");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_Name",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_email_channels_Name",
                table: "email_channels");

            migrationBuilder.DropIndex(
                name: "IX_devices_DeviceName",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_device_pools_Name",
                table: "device_pools");

            migrationBuilder.DropIndex(
                name: "IX_credentials_Name",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_allowed_python_modules_Module",
                table: "allowed_python_modules");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_Name",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_Intent_Platform",
                table: "vendor_commands",
                columns: new[] { "Intent", "Platform" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Name",
                table: "snippets",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_Name",
                table: "profiles",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_policies_Name",
                table: "policies",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_Name",
                table: "messaging_channels",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_articles_Slug",
                table: "knowledge_articles",
                column: "Slug",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_Name",
                table: "inventory_sources",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_Name",
                table: "email_channels",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_devices_DeviceName",
                table: "devices",
                column: "DeviceName",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_Name",
                table: "device_pools",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_Name",
                table: "credentials",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_Module",
                table: "allowed_python_modules",
                column: "Module",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_Name",
                table: "ai_providers",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills",
                column: "Name",
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_Intent_Platform",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_snippets_Name",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_profiles_Name",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_policies_Name",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "IX_messaging_channels_Name",
                table: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_articles_Slug",
                table: "knowledge_articles");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_Name",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_email_channels_Name",
                table: "email_channels");

            migrationBuilder.DropIndex(
                name: "IX_devices_DeviceName",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_device_pools_Name",
                table: "device_pools");

            migrationBuilder.DropIndex(
                name: "IX_credentials_Name",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_allowed_python_modules_Module",
                table: "allowed_python_modules");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_Name",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_Intent_Platform",
                table: "vendor_commands",
                columns: new[] { "Intent", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Name",
                table: "snippets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profiles_Name",
                table: "profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policies_Name",
                table: "policies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_Name",
                table: "messaging_channels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_Name",
                table: "mcp_servers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_articles_Slug",
                table: "knowledge_articles",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_Name",
                table: "inventory_sources",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_Name",
                table: "email_channels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_DeviceName",
                table: "devices",
                column: "DeviceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_Name",
                table: "device_pools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credentials_Name",
                table: "credentials",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_Module",
                table: "allowed_python_modules",
                column: "Module",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_Name",
                table: "ai_providers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills",
                column: "Name",
                unique: true);
        }
    }
}
