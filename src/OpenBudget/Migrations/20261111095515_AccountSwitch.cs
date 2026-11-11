using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261111095515_AccountSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MoneyMoneyImportFinishedAt",
                table: "import_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingRefreshAt",
                table: "bank_login_accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRefreshJson",
                table: "bank_login_accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingSinceUtc",
                table: "bank_login_accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SwitchedAt",
                table: "bank_login_accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "import_sources",
                keyColumn: "Id",
                keyValue: 1,
                column: "MoneyMoneyImportFinishedAt",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MoneyMoneyImportFinishedAt",
                table: "import_sources");

            migrationBuilder.DropColumn(
                name: "PendingRefreshAt",
                table: "bank_login_accounts");

            migrationBuilder.DropColumn(
                name: "PendingRefreshJson",
                table: "bank_login_accounts");

            migrationBuilder.DropColumn(
                name: "PendingSinceUtc",
                table: "bank_login_accounts");

            migrationBuilder.DropColumn(
                name: "SwitchedAt",
                table: "bank_login_accounts");
        }
    }
}
