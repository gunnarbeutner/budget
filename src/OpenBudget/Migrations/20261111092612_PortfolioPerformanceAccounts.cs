using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioPerformanceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRetired",
                table: "pp_portfolio",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "pp_portfolio_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    PpPortfolioUuid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ShowAsAccount = table.Column<bool>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    DepotAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    LedgerAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    TakenOver = table.Column<bool>(type: "INTEGER", nullable: false),
                    TakeoverDeclined = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pp_portfolio_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pp_portfolio_settings_SourceId_PpPortfolioUuid",
                table: "pp_portfolio_settings",
                columns: new[] { "SourceId", "PpPortfolioUuid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pp_portfolio_settings");

            migrationBuilder.DropColumn(
                name: "IsRetired",
                table: "pp_portfolio");
        }
    }
}
