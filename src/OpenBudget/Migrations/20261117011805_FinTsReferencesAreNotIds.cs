using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261117011805_FinTsReferencesAreNotIds : Migration
{
    /// <inheritdoc />
    /// <remarks>
    /// FinTS bookings (camt and MT940) carried the bank reference as their identity, which banks share between bookings.
    /// Without it the next sync matches each of them by day, amount and text and gives it its contents key.
    /// </remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE transactions SET external_id = NULL
            WHERE external_id LIKE 'id:%'
              AND local_account_key IN (SELECT Id FROM accounts WHERE provider IN ('ing', 'psd-nuernberg', 'baader'))
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
