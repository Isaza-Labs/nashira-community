using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase10_AuditActorAndHashVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "audit_events",
                type: "text",
                nullable: true);

            // 1, not the EF default of 0. Every row already on disk was signed with the
            // canonical form that predates Actor, and that IS version 1. Leaving them at
            // 0 would have them declare a version this build cannot verify, so /verify
            // would report the whole trail broken the moment this migration ran —
            // reading as tampering where nothing had been tampered with.
            migrationBuilder.AddColumn<int>(
                name: "HashVersion",
                table: "audit_events",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Actor",
                table: "audit_events",
                column: "Actor");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_EntityId",
                table: "audit_events",
                column: "EntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_events_Actor",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_At",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_EntityId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "HashVersion",
                table: "audit_events");
        }
    }
}
