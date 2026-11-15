using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115100500_AccountRole : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "role",
            table: "accounts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.Sql(BackfillSql);
    }

    /// <summary>
    /// Gives accounts linked to a connector login the role that connector sets on sync: PayPal accounts are payment
    /// wallets, Kraken accounts and Scalable's non-depot accounts broker cash accounts.
    /// </summary>
    public const string BackfillSql = """
        UPDATE accounts SET role = CASE
            (SELECT l.ConnectorType FROM bank_login_accounts la JOIN bank_logins l ON l.Id = la.BankLoginId
             WHERE la.LinkedAccountId = accounts.Id LIMIT 1)
            WHEN 'paypal' THEN 1
            WHEN 'kraken' THEN 2
            WHEN 'scalable' THEN CASE WHEN type = 7 OR portfolio = 1 THEN 0 ELSE 2 END
            ELSE role END
        WHERE role = 0;
        """;

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "role",
            table: "accounts");
    }
}
