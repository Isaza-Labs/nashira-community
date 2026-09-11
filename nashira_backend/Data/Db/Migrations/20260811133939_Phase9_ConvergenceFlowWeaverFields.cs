using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_ConvergenceFlowWeaverFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "workflows",
                type: "uuid",
                nullable: true);

            // The allow trio is a NEW restriction over an inventory that has never had
            // one, so the backfill has to reproduce the old behaviour exactly: before
            // the trio existed, a draft or production run reached every device. Taking
            // EF's scaffolded `defaultValue: false` here would park every existing
            // device — no run could target anything — which is a silent outage, not a
            // migration. AllowQa is the one that legitimately starts false: qa is opt-in.
            migrationBuilder.AddColumn<bool>(
                name: "AllowDraft",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValueSql: "true");

            migrationBuilder.AddColumn<bool>(
                name: "AllowProduction",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValueSql: "true");

            migrationBuilder.AddColumn<bool>(
                name: "AllowQa",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false");

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncAt",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            // EF scaffolded `JsonDocument.Parse("")` for the default, which throws at
            // migration time — the empty string is not JSON. The column is NOT NULL, so
            // existing rows need a real default: an empty object, the same value the
            // entity seeds for new rows.
            migrationBuilder.AddColumn<JsonElement>(
                name: "Properties",
                table: "devices",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "ai_prompt_skills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "ai_prompt_skills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "ai_api_specs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_IntegrationId",
                table: "ai_prompt_skills",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_IsActive_Priority_Name",
                table: "ai_prompt_skills",
                columns: new[] { "IsActive", "Priority", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_IntegrationId",
                table: "ai_api_specs",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_IsActive",
                table: "ai_api_specs",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_IntegrationId",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_IsActive_Priority_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_IntegrationId",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_IsActive",
                table: "ai_api_specs");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "AllowDraft",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "AllowProduction",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "AllowQa",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "LastSyncAt",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "Properties",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "ai_api_specs");
        }
    }
}
