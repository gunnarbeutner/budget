using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionEditingAndStructureTakeover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_reference",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "booking_text",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "category_set_at",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category_set_by",
                table: "transactions",
                type: "TEXT",
                nullable: false,
                defaultValue: "import");

            migrationBuilder.AddColumn<bool>(
                name: "checked",
                table: "transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "counterparty_bic",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "counterparty_iban",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_new",
                table: "transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rule_id",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "value_timestamp",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StructureTakenOverAt",
                table: "import_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_in_charts",
                table: "categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // MoneyMoney-owned rows mirror MoneyMoney's comment and check mark (re-done on every upload).
            migrationBuilder.Sql("UPDATE transactions SET note = comment, \"checked\" = (COALESCE(checkmark, 0) > 0)");

            migrationBuilder.UpdateData(
                table: "import_sources",
                keyColumn: "Id",
                keyValue: 1,
                column: "StructureTakenOverAt",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bank_reference",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "booking_text",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "category_set_at",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "category_set_by",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "checked",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "counterparty_bic",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "counterparty_iban",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "is_new",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "note",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "rule_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "value_timestamp",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "StructureTakenOverAt",
                table: "import_sources");

            migrationBuilder.DropColumn(
                name: "show_in_charts",
                table: "categories");
        }
    }
}
