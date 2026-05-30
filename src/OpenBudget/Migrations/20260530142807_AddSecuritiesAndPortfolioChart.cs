using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260530142807_AddSecuritiesAndPortfolioChart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mm_portfolio_chart",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    account_key = table.Column<long>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", nullable: true),
                    timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    balance = table.Column<double>(type: "REAL", nullable: true),
                    securities_key = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_portfolio_chart", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_portfolio_chart_mm_accounts_account_key",
                        column: x => x.account_key,
                        principalTable: "mm_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mm_securities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    account_key = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    isin = table.Column<string>(type: "TEXT", nullable: true),
                    wkn = table.Column<string>(type: "TEXT", nullable: true),
                    symbol = table.Column<string>(type: "TEXT", nullable: true),
                    quantity = table.Column<double>(type: "REAL", nullable: false),
                    amount = table.Column<double>(type: "REAL", nullable: true),
                    amount_cur = table.Column<string>(type: "TEXT", nullable: true),
                    price = table.Column<double>(type: "REAL", nullable: true),
                    price_cur = table.Column<string>(type: "TEXT", nullable: true),
                    purchase = table.Column<double>(type: "REAL", nullable: true),
                    asset_class = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_securities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_securities_mm_accounts_account_key",
                        column: x => x.account_key,
                        principalTable: "mm_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mm_portfolio_chart_account_key",
                table: "mm_portfolio_chart",
                column: "account_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_portfolio_chart_SourceId_account_key_timestamp",
                table: "mm_portfolio_chart",
                columns: new[] { "SourceId", "account_key", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_mm_securities_account_key",
                table: "mm_securities",
                column: "account_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_securities_SourceId_account_key",
                table: "mm_securities",
                columns: new[] { "SourceId", "account_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mm_portfolio_chart");

            migrationBuilder.DropTable(
                name: "mm_securities");
        }
    }
}
