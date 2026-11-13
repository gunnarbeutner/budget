using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261113102750_AmazonOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_items",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SplitJson",
                table: "category_suggestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EnrichAccountId",
                table: "bank_login_accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "amazon_charges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    InstrumentId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instrument = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
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
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OrderDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalCents = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    DetailsFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AmazonOrderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    Shipment = table.Column<int>(type: "INTEGER", nullable: false),
                    Asin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: true)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "amazon_charges");

            migrationBuilder.DropTable(
                name: "amazon_order_items");

            migrationBuilder.DropTable(
                name: "amazon_orders");

            migrationBuilder.DropColumn(
                name: "order_items",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "SplitJson",
                table: "category_suggestions");

            migrationBuilder.DropColumn(
                name: "EnrichAccountId",
                table: "bank_login_accounts");
        }
    }
}
