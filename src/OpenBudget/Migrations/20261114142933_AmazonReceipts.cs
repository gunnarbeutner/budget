using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114142933_AmazonReceipts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The former Amazon tables (amazon_orders, amazon_order_items, amazon_charges) stay until AmazonReceiptMigration
        // has copied them into the receipts at startup; it drops them then.
        migrationBuilder.AddColumn<int>(
            name: "Shipment",
            table: "receipt_items",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BankLoginId",
            table: "receipt_invoices",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "DetailsReadAt",
            table: "receipt_invoices",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "OrderTotalCents",
            table: "receipt_invoices",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentMethod",
            table: "receipt_invoices",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentMethodId",
            table: "receipt_invoices",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceKey",
            table: "receipt_invoices",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_receipt_invoices_BankLoginId",
            table: "receipt_invoices",
            column: "BankLoginId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_receipt_invoices_BankLoginId",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "Shipment",
            table: "receipt_items");

        migrationBuilder.DropColumn(
            name: "BankLoginId",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "DetailsReadAt",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "OrderTotalCents",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "PaymentMethod",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "PaymentMethodId",
            table: "receipt_invoices");

        migrationBuilder.DropColumn(
            name: "SourceKey",
            table: "receipt_invoices");

        migrationBuilder.CreateTable(
            name: "amazon_charges",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                ChargeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                Instrument = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                InstrumentId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                OrderId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                TransactionId = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_amazon_charges", x => x.Id);
                table.ForeignKey(
                    name: "FK_amazon_charges_bank_logins_BankLoginId",
                    column: x => x.BankLoginId,
                    principalTable: "bank_logins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "amazon_orders",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                DetailsFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                OrderDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                OrderId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                TotalCents = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_amazon_orders", x => x.Id);
                table.ForeignKey(
                    name: "FK_amazon_orders_bank_logins_BankLoginId",
                    column: x => x.BankLoginId,
                    principalTable: "bank_logins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "amazon_order_items",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AmazonOrderId = table.Column<long>(type: "INTEGER", nullable: false),
                Asin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                PriceCents = table.Column<long>(type: "INTEGER", nullable: true),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                Shipment = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_amazon_order_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_amazon_order_items_amazon_orders_AmazonOrderId",
                    column: x => x.AmazonOrderId,
                    principalTable: "amazon_orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_amazon_charges_BankLoginId_ChargeKey",
            table: "amazon_charges",
            columns: new[] { "BankLoginId", "ChargeKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_amazon_charges_TransactionId",
            table: "amazon_charges",
            column: "TransactionId");

        migrationBuilder.CreateIndex(
            name: "IX_amazon_order_items_AmazonOrderId",
            table: "amazon_order_items",
            column: "AmazonOrderId");

        migrationBuilder.CreateIndex(
            name: "IX_amazon_orders_BankLoginId_OrderId",
            table: "amazon_orders",
            columns: new[] { "BankLoginId", "OrderId" },
            unique: true);
    }
}
