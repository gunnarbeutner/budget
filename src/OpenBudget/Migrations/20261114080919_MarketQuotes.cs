using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114080919_MarketQuotes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "price_time",
            table: "holdings",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "market_quote",
            columns: table => new
            {
                Symbol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Day = table.Column<long>(type: "INTEGER", nullable: false),
                Close = table.Column<double>(type: "REAL", nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_market_quote", x => new { x.Symbol, x.Day });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "market_quote");

        migrationBuilder.DropColumn(
            name: "price_time",
            table: "holdings");
    }
}
