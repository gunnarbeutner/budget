using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261111224030_AccountInCashflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InCashflow",
                table: "project_account_assignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Scope 2 ("informational") becomes an all-bookings assignment outside the cashflow.
            migrationBuilder.Sql("UPDATE project_account_assignments SET InCashflow = 0, Scope = 1 WHERE Scope = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE project_account_assignments SET Scope = 2 WHERE InCashflow = 0;");
            migrationBuilder.DropColumn(
                name: "InCashflow",
                table: "project_account_assignments");
        }
    }
}
