using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase8_RemoveMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropIndex(
                name: "IX_workflows_CompanyId",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflows_CompanyId_Environment",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflows_CompanyId_Name",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_CompanyId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_CompanyId_WorkflowId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_validation_records_CompanyId",
                table: "validation_records");

            migrationBuilder.DropIndex(
                name: "IX_validation_records_CompanyId_Kind",
                table: "validation_records");

            migrationBuilder.DropIndex(
                name: "IX_users_CompanyId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_CompanyId_Username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_user_tool_permissions_CompanyId",
                table: "user_tool_permissions");

            migrationBuilder.DropIndex(
                name: "IX_user_tool_permissions_CompanyId_UserId_ToolDomain",
                table: "user_tool_permissions");

            migrationBuilder.DropIndex(
                name: "IX_system_settings_CompanyId",
                table: "system_settings");

            migrationBuilder.DropIndex(
                name: "IX_system_settings_CompanyId_Provider_SettingKey",
                table: "system_settings");

            migrationBuilder.DropIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs");

            migrationBuilder.DropIndex(
                name: "IX_step_runs_CompanyId_WorkflowRunId",
                table: "step_runs");

            migrationBuilder.DropIndex(
                name: "IX_simulation_results_CompanyId",
                table: "simulation_results");

            migrationBuilder.DropIndex(
                name: "IX_simulation_results_CompanyId_WorkflowId",
                table: "simulation_results");

            migrationBuilder.DropIndex(
                name: "IX_secrets_CompanyId",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_secrets_CompanyId_Provider_SettingKey",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_profiles_CompanyId",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_profiles_CompanyId_Name",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_articles_CompanyId",
                table: "knowledge_articles");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_articles_CompanyId_Slug",
                table: "knowledge_articles");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_CompanyId_Name",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_CompanyId",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_CompanyId_Name",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_export_artifacts_CompanyId",
                table: "export_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId_DeviceName",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_CompanyId_Name",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_CompanyId_EntityType",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_CompanyId_Sequence",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_CompanyId_Name",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_CompanyId_UserId",
                table: "ai_conversations");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_agent_learnings_CompanyId",
                table: "agent_learnings");

            migrationBuilder.DropIndex(
                name: "IX_agent_learnings_CompanyId_Category_Confidence",
                table: "agent_learnings");

            migrationBuilder.DropIndex(
                name: "IX_agent_learnings_CompanyId_ToolName",
                table: "agent_learnings");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "validation_records");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "user_tool_permissions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "simulation_results");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "knowledge_articles");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "git_repositories");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "export_artifacts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_providers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_api_specs");

            // Add the flag first, then carry over the old sentinel while CompanyId
            // still exists: system knowledge was CompanyId = '00000000-...'.
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "agent_learnings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE agent_learnings SET ""IsSystem"" = TRUE
                  WHERE ""CompanyId"" = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "agent_learnings");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_Environment",
                table: "workflows",
                column: "Environment");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_Name",
                table: "workflows",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId",
                table: "workflow_runs",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_validation_records_Kind",
                table: "validation_records",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tool_permissions_UserId_ToolDomain",
                table: "user_tool_permissions",
                columns: new[] { "UserId", "ToolDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_Provider_SettingKey",
                table: "system_settings",
                columns: new[] { "Provider", "SettingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_WorkflowRunId",
                table: "step_runs",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_WorkflowId",
                table: "simulation_results",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Provider_SettingKey",
                table: "secrets",
                columns: new[] { "Provider", "SettingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profiles_Name",
                table: "profiles",
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
                name: "IX_devices_DeviceName",
                table: "devices",
                column: "DeviceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credentials_Name",
                table: "credentials",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_EntityType",
                table: "audit_events",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Sequence",
                table: "audit_events",
                column: "Sequence",
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

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_UserId",
                table: "ai_conversations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs",
                column: "Api",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_Category_Confidence",
                table: "agent_learnings",
                columns: new[] { "Category", "Confidence" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_ToolName",
                table: "agent_learnings",
                column: "ToolName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflows_Environment",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflows_Name",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_WorkflowId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_validation_records_Kind",
                table: "validation_records");

            migrationBuilder.DropIndex(
                name: "IX_users_Username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_user_tool_permissions_UserId_ToolDomain",
                table: "user_tool_permissions");

            migrationBuilder.DropIndex(
                name: "IX_system_settings_Provider_SettingKey",
                table: "system_settings");

            migrationBuilder.DropIndex(
                name: "IX_step_runs_WorkflowRunId",
                table: "step_runs");

            migrationBuilder.DropIndex(
                name: "IX_simulation_results_WorkflowId",
                table: "simulation_results");

            migrationBuilder.DropIndex(
                name: "IX_secrets_Provider_SettingKey",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_profiles_Name",
                table: "profiles");

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
                name: "IX_devices_DeviceName",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_credentials_Name",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_EntityType",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_Sequence",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_Name",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_UserId",
                table: "ai_conversations");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_agent_learnings_Category_Confidence",
                table: "agent_learnings");

            migrationBuilder.DropIndex(
                name: "IX_agent_learnings_ToolName",
                table: "agent_learnings");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "agent_learnings");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflows",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "validation_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "user_tool_permissions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "system_settings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "step_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "simulation_results",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "secrets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "profiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "knowledge_articles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "inventory_sources",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "git_repositories",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "export_artifacts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "devices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "credentials",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "audit_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_providers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_prompt_skills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_api_specs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "agent_learnings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.CompanyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflows_CompanyId",
                table: "workflows",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflows_CompanyId_Environment",
                table: "workflows",
                columns: new[] { "CompanyId", "Environment" });

            migrationBuilder.CreateIndex(
                name: "IX_workflows_CompanyId_Name",
                table: "workflows",
                columns: new[] { "CompanyId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_CompanyId",
                table: "workflow_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_CompanyId_WorkflowId",
                table: "workflow_runs",
                columns: new[] { "CompanyId", "WorkflowId" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_records_CompanyId",
                table: "validation_records",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_validation_records_CompanyId_Kind",
                table: "validation_records",
                columns: new[] { "CompanyId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_users_CompanyId",
                table: "users",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_users_CompanyId_Username",
                table: "users",
                columns: new[] { "CompanyId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tool_permissions_CompanyId",
                table: "user_tool_permissions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_user_tool_permissions_CompanyId_UserId_ToolDomain",
                table: "user_tool_permissions",
                columns: new[] { "CompanyId", "UserId", "ToolDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_CompanyId",
                table: "system_settings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_CompanyId_Provider_SettingKey",
                table: "system_settings",
                columns: new[] { "CompanyId", "Provider", "SettingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_CompanyId_WorkflowRunId",
                table: "step_runs",
                columns: new[] { "CompanyId", "WorkflowRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_CompanyId",
                table: "simulation_results",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_CompanyId_WorkflowId",
                table: "simulation_results",
                columns: new[] { "CompanyId", "WorkflowId" });

            migrationBuilder.CreateIndex(
                name: "IX_secrets_CompanyId",
                table: "secrets",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_CompanyId_Provider_SettingKey",
                table: "secrets",
                columns: new[] { "CompanyId", "Provider", "SettingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profiles_CompanyId",
                table: "profiles",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_CompanyId_Name",
                table: "profiles",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_articles_CompanyId",
                table: "knowledge_articles",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_articles_CompanyId_Slug",
                table: "knowledge_articles",
                columns: new[] { "CompanyId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_CompanyId_Name",
                table: "inventory_sources",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_CompanyId",
                table: "git_repositories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_CompanyId_Name",
                table: "git_repositories",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_artifacts_CompanyId",
                table: "export_artifacts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId",
                table: "devices",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId_DeviceName",
                table: "devices",
                columns: new[] { "CompanyId", "DeviceName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_CompanyId_Name",
                table: "credentials",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CompanyId_EntityType",
                table: "audit_events",
                columns: new[] { "CompanyId", "EntityType" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CompanyId_Sequence",
                table: "audit_events",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_CompanyId_Name",
                table: "ai_providers",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_CompanyId_UserId",
                table: "ai_conversations",
                columns: new[] { "CompanyId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "Api" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId",
                table: "agent_learnings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId_Category_Confidence",
                table: "agent_learnings",
                columns: new[] { "CompanyId", "Category", "Confidence" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_learnings_CompanyId_ToolName",
                table: "agent_learnings",
                columns: new[] { "CompanyId", "ToolName" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_Slug",
                table: "companies",
                column: "Slug",
                unique: true);
        }
    }
}
