using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114200632_AmazonSharedCharges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OtherOrderNumbers",
            table: "receipt_invoices",
            type: "TEXT",
            maxLength: 400,
            nullable: true);

        // The next Amazon sync reads the whole history again, so charges of several orders get all their orders' items.
        migrationBuilder.Sql("UPDATE bank_logins SET OrderHistoryCompleteAt = NULL WHERE ConnectorType = 'amazon';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OtherOrderNumbers",
            table: "receipt_invoices");
    }
}
