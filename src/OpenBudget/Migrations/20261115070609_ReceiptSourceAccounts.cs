using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115070609_ReceiptSourceAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "receipt_source_accounts",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_source_accounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_receipt_source_accounts_accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_receipt_source_accounts_AccountId",
            table: "receipt_source_accounts",
            column: "AccountId");

        migrationBuilder.CreateIndex(
            name: "IX_receipt_source_accounts_OwnerUserId_Source_AccountId",
            table: "receipt_source_accounts",
            columns: new[] { "OwnerUserId", "Source", "AccountId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "receipt_source_accounts");
    }
}
