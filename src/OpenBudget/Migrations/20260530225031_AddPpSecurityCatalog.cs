using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260530225031_AddPpSecurityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pp_security",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Isin = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TickerSymbol = table.Column<string>(type: "TEXT", nullable: true),
                    Wkn = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pp_security", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pp_security_SourceId_Isin",
                table: "pp_security",
                columns: new[] { "SourceId", "Isin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pp_security");
        }
    }
}
