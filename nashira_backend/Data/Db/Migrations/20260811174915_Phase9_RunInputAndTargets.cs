using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_RunInputAndTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputJson",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            // EF scaffolded `defaultValue: ""` for this NOT NULL column, which is not
            // valid JSON — every run that predates this migration would then throw
            // when its targets are read back. The column holds an array, so the
            // empty value is an empty array.
            migrationBuilder.AddColumn<string>(
                name: "TargetDevicesJson",
                table: "workflow_runs",
                type: "text",
                nullable: false,
                defaultValueSql: "'[]'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputJson",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "TargetDevicesJson",
                table: "workflow_runs");
        }
    }
}
