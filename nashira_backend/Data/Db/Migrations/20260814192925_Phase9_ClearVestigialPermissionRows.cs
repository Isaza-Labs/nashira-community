using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase9_ClearVestigialPermissionRows : Migration
    {
        // Data migration, no schema change.
        //
        // Permission rows used to be read by nothing, and the old admin screen wrote one
        // row per domain on every save — including the domains left unchecked, which
        // landed as can_read/can_write/can_execute all false. That was harmless while
        // nothing enforced them.
        //
        // ToolResourceGuard now enforces them, and a row granting nothing is a total
        // denial of that target. Anyone who ever pressed "Save permissions" would come
        // out of this upgrade locked out of whatever they had not ticked — a lockout
        // nobody chose, produced by a screen that could not express "no opinion".
        //
        // So the rows that grant nothing are removed. They cannot represent a deliberate
        // denial: no deliberate denial was possible before this release. Restrictions
        // written from here on are explicit, and `inherit` is how they are lifted.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM user_tool_permissions
                WHERE "CanRead" = false AND "CanWrite" = false AND "CanExecute" = false;
                """);
        }

        // Not reversible, and deliberately so: restoring rows that denied everything
        // would restore the lockout this removed.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
