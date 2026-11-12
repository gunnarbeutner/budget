using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261112001816_AddScheduledPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduledError",
                table: "bank_login_accounts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "scheduled_payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    BankOrderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NextDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FirstDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    IntervalUnit = table.Column<int>(type: "INTEGER", nullable: true),
                    IntervalCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ExecutionDay = table.Column<int>(type: "INTEGER", nullable: true),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false, defaultValue: "EUR"),
                    CounterpartyName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CounterpartyIban = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CounterpartyBic = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scheduled_payments_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_payments_AccountId_NextDate",
                table: "scheduled_payments",
                columns: new[] { "AccountId", "NextDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_payments");

            migrationBuilder.DropColumn(
                name: "ScheduledError",
                table: "bank_login_accounts");
        }
    }
}
