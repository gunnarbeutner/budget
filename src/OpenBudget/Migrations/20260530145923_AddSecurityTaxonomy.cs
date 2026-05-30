using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260530145923_AddSecurityTaxonomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pp_security_taxonomy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Isin = table.Column<string>(type: "TEXT", nullable: false),
                    TaxonomyId = table.Column<string>(type: "TEXT", nullable: false),
                    TaxonomyName = table.Column<string>(type: "TEXT", nullable: false),
                    ClassificationId = table.Column<string>(type: "TEXT", nullable: false),
                    ClassificationName = table.Column<string>(type: "TEXT", nullable: false),
                    ClassificationPath = table.Column<string>(type: "TEXT", nullable: false),
                    WeightBp = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pp_security_taxonomy", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pp_security_taxonomy_SourceId_Isin",
                table: "pp_security_taxonomy",
                columns: new[] { "SourceId", "Isin" });

            migrationBuilder.CreateIndex(
                name: "IX_pp_security_taxonomy_SourceId_TaxonomyId",
                table: "pp_security_taxonomy",
                columns: new[] { "SourceId", "TaxonomyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pp_security_taxonomy");
        }
    }
}
