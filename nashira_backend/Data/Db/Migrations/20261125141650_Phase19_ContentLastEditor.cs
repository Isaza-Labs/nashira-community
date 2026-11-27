using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase19_ContentLastEditor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "workflow_triggers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "snippets",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "workflow_triggers");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "snippets");
        }
    }
}
