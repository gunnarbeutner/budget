using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115104230_ReceiptProductIdentities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "receipt_product_identities",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Shop = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                NameKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Brand = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                PackCount = table.Column<int>(type: "INTEGER", nullable: true),
                UnitSize = table.Column<decimal>(type: "TEXT", nullable: true),
                Unit = table.Column<int>(type: "INTEGER", nullable: true),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<int>(type: "INTEGER", nullable: false),
                Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_product_identities", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_receipt_product_identities_OwnerUserId_Shop_NameKey",
            table: "receipt_product_identities",
            columns: new[] { "OwnerUserId", "Shop", "NameKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "receipt_product_identities");
    }
}
