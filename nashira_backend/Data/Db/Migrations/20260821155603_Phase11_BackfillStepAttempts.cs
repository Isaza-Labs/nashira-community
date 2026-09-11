using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_BackfillStepAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase11_RunDiagnostics added step_runs.Attempts with a default of 0, and
            // the run panel reads 0 as "this step never reached its handler". Every row
            // written before that migration therefore claimed it never ran — including
            // steps sitting next to their own output, which is a screen contradicting
            // itself.
            //
            // A step_runs row exists because the executor recorded an execution, so 1
            // is the honest value for anything already stored. The blanket WHERE is
            // safe: migrations run at boot before the app serves traffic, so no row
            // written by the new code can be sitting at 0 by the time this executes.
            migrationBuilder.Sql(@"UPDATE step_runs SET ""Attempts"" = 1 WHERE ""Attempts"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only, and the original values are not recoverable — nothing to undo.
        }
    }
}
