using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <summary>
/// Accounts a MoneyMoney import brought are manual accounts. An imported account linked to a bank connection becomes
/// that connection's (its first sync matches the bank's rows from its newest booking on); every other one becomes
/// manual. The import's "finished" flag and the bank reports kept for links waiting for their switch go.
/// </summary>
public partial class _20261114083120_ImportedAccountsManual : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "bank_login_accounts"
            SET "SwitchedAt" = COALESCE("SwitchedAt", strftime('%Y-%m-%d %H:%M:%S', 'now')),
                "LastSuccessAt" = NULL, "SyncFloorUtc" = NULL, "LastError" = NULL
            WHERE "LinkedAccountId" IN (SELECT "Id" FROM "accounts" WHERE "provider" = 'moneymoney');
            """);
        migrationBuilder.Sql(
            """
            UPDATE "accounts"
            SET "provider" = (SELECT l."ConnectorType" FROM "bank_login_accounts" a JOIN "bank_logins" l ON l."Id" = a."BankLoginId"
                              WHERE a."LinkedAccountId" = "accounts"."Id" ORDER BY a."Id" LIMIT 1)
            WHERE "provider" = 'moneymoney'
              AND EXISTS (SELECT 1 FROM "bank_login_accounts" a WHERE a."LinkedAccountId" = "accounts"."Id");
            """);
        migrationBuilder.Sql("""UPDATE "accounts" SET "provider" = 'manual' WHERE "provider" = 'moneymoney';""");

        // SQLite drops these columns in place (EF's DropColumn would rebuild the tables). The provider column keeps its
        // database default: every insert names the provider.
        migrationBuilder.Sql("""ALTER TABLE "user_data_states" DROP COLUMN "MoneyMoneyImportFinishedAt";""");
        migrationBuilder.Sql("""ALTER TABLE "bank_login_accounts" DROP COLUMN "PendingRefreshAt";""");
        migrationBuilder.Sql("""ALTER TABLE "bank_login_accounts" DROP COLUMN "PendingRefreshJson";""");
        migrationBuilder.Sql("""ALTER TABLE "bank_login_accounts" DROP COLUMN "PendingSinceUtc";""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "MoneyMoneyImportFinishedAt",
            table: "user_data_states",
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
    }
}
