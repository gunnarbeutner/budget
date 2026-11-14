using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114105421_ReceiptInvoices : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "MailAccess",
            table: "cloud_connections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "item_category_memory",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_item_category_memory", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "receipt_invoices",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Shop = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                InvoiceNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                OrderNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                OrderDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                DeliveryDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                InvoiceDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                TotalCents = table.Column<long>(type: "INTEGER", nullable: false),
                BonusCents = table.Column<long>(type: "INTEGER", nullable: false),
                PaymentCents = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ReadAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                TransactionId = table.Column<long>(type: "INTEGER", nullable: true),
                SplitSignature = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_invoices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "receipt_settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                NonFoodCategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                MailCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                MailAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                MailLastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_settings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "receipt_items",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ReceiptInvoiceId = table.Column<long>(type: "INTEGER", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                Quantity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                VatCode = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                UnitCents = table.Column<long>(type: "INTEGER", nullable: true),
                TotalCents = table.Column<long>(type: "INTEGER", nullable: false),
                NoDiscount = table.Column<bool>(type: "INTEGER", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                CategorySource = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_receipt_items_receipt_invoices_ReceiptInvoiceId",
                    column: x => x.ReceiptInvoiceId,
                    principalTable: "receipt_invoices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_item_category_memory_OwnerUserId_NormalizedName",
            table: "item_category_memory",
            columns: new[] { "OwnerUserId", "NormalizedName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_receipt_invoices_OwnerUserId_Shop_InvoiceNumber",
            table: "receipt_invoices",
            columns: new[] { "OwnerUserId", "Shop", "InvoiceNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_receipt_invoices_TransactionId",
            table: "receipt_invoices",
            column: "TransactionId");

        migrationBuilder.CreateIndex(
            name: "IX_receipt_items_ReceiptInvoiceId",
            table: "receipt_items",
            column: "ReceiptInvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_receipt_settings_OwnerUserId",
            table: "receipt_settings",
            column: "OwnerUserId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "item_category_memory");

        migrationBuilder.DropTable(
            name: "receipt_items");

        migrationBuilder.DropTable(
            name: "receipt_settings");

        migrationBuilder.DropTable(
            name: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "MailAccess",
            table: "cloud_connections");
    }
}
