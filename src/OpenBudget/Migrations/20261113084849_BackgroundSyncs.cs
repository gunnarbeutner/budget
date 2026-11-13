using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261113084849_BackgroundSyncs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BackgroundEnabled",
                table: "bank_logins",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "BackgroundFailures",
                table: "bank_logins",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastBackgroundSyncAt",
                table: "bank_logins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NeedsManualSince",
                table: "bank_logins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NeedsManualSync",
                table: "bank_logins",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextBackgroundAttemptAt",
                table: "bank_logins",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "background_sync_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Background = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FinTsCodes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Blz = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_sync_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_background_sync_runs_bank_logins_BankLoginId",
                        column: x => x.BankLoginId,
                        principalTable: "bank_logins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fints_bank_settings",
                columns: table => new
                {
                    Blz = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BackgroundEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fints_bank_settings", x => x.Blz);
                });

            migrationBuilder.CreateIndex(
                name: "IX_background_sync_runs_BankLoginId_StartedAt",
                table: "background_sync_runs",
                columns: new[] { "BankLoginId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_background_sync_runs_Blz_StartedAt",
                table: "background_sync_runs",
                columns: new[] { "Blz", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_sync_runs");

            migrationBuilder.DropTable(
                name: "fints_bank_settings");

            migrationBuilder.DropColumn(
                name: "BackgroundEnabled",
                table: "bank_logins");

            migrationBuilder.DropColumn(
                name: "BackgroundFailures",
                table: "bank_logins");

            migrationBuilder.DropColumn(
                name: "LastBackgroundSyncAt",
                table: "bank_logins");

            migrationBuilder.DropColumn(
                name: "NeedsManualSince",
                table: "bank_logins");

            migrationBuilder.DropColumn(
                name: "NeedsManualSync",
                table: "bank_logins");

            migrationBuilder.DropColumn(
                name: "NextBackgroundAttemptAt",
                table: "bank_logins");
        }
    }
}
