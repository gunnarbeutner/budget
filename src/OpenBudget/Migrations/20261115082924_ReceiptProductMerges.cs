using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115082924_ReceiptProductMerges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "receipt_product_merges",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Shop = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                FromKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                ToKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_product_merges", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_receipt_product_merges_OwnerUserId_Shop_FromKey",
            table: "receipt_product_merges",
            columns: new[] { "OwnerUserId", "Shop", "FromKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "receipt_product_merges");
    }
}
