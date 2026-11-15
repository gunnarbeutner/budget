using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115143442_DropReceiptProducts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "receipt_product_identities");

        migrationBuilder.DropTable(
            name: "receipt_product_merges");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "receipt_product_identities",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Brand = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                NameKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                PackCount = table.Column<int>(type: "INTEGER", nullable: true),
                ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Shop = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Source = table.Column<int>(type: "INTEGER", nullable: false),
                Unit = table.Column<int>(type: "INTEGER", nullable: true),
                UnitSize = table.Column<decimal>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_product_identities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "receipt_product_merges",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                FromKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Shop = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ToKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_product_merges", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_receipt_product_identities_OwnerUserId_Shop_NameKey",
            table: "receipt_product_identities",
            columns: new[] { "OwnerUserId", "Shop", "NameKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_receipt_product_merges_OwnerUserId_Shop_FromKey",
            table: "receipt_product_merges",
            columns: new[] { "OwnerUserId", "Shop", "FromKey" },
            unique: true);
    }
}
