using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <summary>
    /// Switch mm_transactions.amount from REAL euros to INTEGER cents so DB-side SUM is
    /// exact instead of accumulating IEEE rounding error. Existing rows are multiplied by
    /// 100 (banker's rounding via SQLite's ROUND) before AlterColumn converts REAL → INTEGER;
    /// CAST(REAL → INTEGER) truncates, but after the multiply every value is integer-valued
    /// so truncation is exact. New imports also write cents (see MoneyMoneyImportService).
    /// </summary>
    public partial class MoneyAsCents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Multiply existing REAL values by 100 in place. After this every row holds an
            // integer-valued REAL (e.g. 1234.0 for €12.34) — safe to cast to INTEGER below.
            migrationBuilder.Sql("UPDATE mm_transactions SET amount = ROUND(amount * 100);");

            migrationBuilder.AlterColumn<long>(
                name: "amount",
                table: "mm_transactions",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "amount",
                table: "mm_transactions",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            // After widening back to REAL, divide to restore the original euro amounts.
            migrationBuilder.Sql("UPDATE mm_transactions SET amount = amount / 100.0;");
        }
    }
}
