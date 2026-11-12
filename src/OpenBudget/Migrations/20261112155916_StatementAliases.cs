using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261112155916_StatementAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_statement_aliases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatementId = table.Column<long>(type: "INTEGER", nullable: false),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_statement_aliases_bank_statements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "bank_statements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_aliases_BankLoginId_Identifier",
                table: "bank_statement_aliases",
                columns: new[] { "BankLoginId", "Identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_aliases_StatementId",
                table: "bank_statement_aliases",
                column: "StatementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_statement_aliases");
        }
    }
}
