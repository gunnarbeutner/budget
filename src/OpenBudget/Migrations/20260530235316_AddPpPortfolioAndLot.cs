using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260530235316_AddPpPortfolioAndLot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pp_lot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    PortfolioUuid = table.Column<string>(type: "TEXT", nullable: false),
                    Isin = table.Column<string>(type: "TEXT", nullable: false),
                    Acquired = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Shares = table.Column<decimal>(type: "TEXT", nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pp_lot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pp_portfolio",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Uuid = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pp_portfolio", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pp_lot_SourceId_Isin",
                table: "pp_lot",
                columns: new[] { "SourceId", "Isin" });

            migrationBuilder.CreateIndex(
                name: "IX_pp_portfolio_SourceId",
                table: "pp_portfolio",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pp_lot");

            migrationBuilder.DropTable(
                name: "pp_portfolio");
        }
    }
}
