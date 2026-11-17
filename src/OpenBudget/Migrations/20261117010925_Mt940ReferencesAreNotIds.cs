using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261117010925_Mt940ReferencesAreNotIds : Migration
{
    /// <inheritdoc />
    /// <remarks>
    /// ING's bookings carried MT940 bank references as their identity, which ING shares between bookings of one type.
    /// Without it the next sync matches each of them by day, amount and text and gives it its contents key.
    /// </remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE transactions SET external_id = NULL
            WHERE external_id LIKE 'id:%'
              AND local_account_key IN (SELECT Id FROM accounts WHERE provider = 'ing')
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
