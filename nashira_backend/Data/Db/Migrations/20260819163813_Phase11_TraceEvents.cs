using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_TraceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trace_events",
                columns: table => new
                {
                    TraceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: true),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trace_events", x => x.TraceEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_Action",
                table: "trace_events",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_At",
                table: "trace_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_Category",
                table: "trace_events",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_RequestId",
                table: "trace_events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_Status",
                table: "trace_events",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_UserId",
                table: "trace_events",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trace_events");
        }
    }
}
