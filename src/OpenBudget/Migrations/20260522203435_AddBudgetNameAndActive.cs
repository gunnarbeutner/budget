using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetNameAndActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "budgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "budgets",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Backfill: name every existing budget with its creation date (YYYY-MM-DD), and mark
            // the newest budget for each (ProjectId, Year) as active so the dashboard keeps
            // rolling up the same data after the migration runs.
            migrationBuilder.Sql(@"
                UPDATE budgets
                SET Name = COALESCE(SUBSTR(CreatedAt, 1, 10), 'Budget')
                WHERE Name = '' OR Name IS NULL;
            ");
            migrationBuilder.Sql(@"
                UPDATE budgets
                SET IsActive = 1
                WHERE Id IN (
                    SELECT MAX(Id) FROM budgets GROUP BY ProjectId, Year
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "budgets");
        }
    }
}
