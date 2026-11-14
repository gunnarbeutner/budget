using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114134208_ReceiptSourceNoDefault : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Default-category rows do not become categories to split into; the column is dropped in place.
        migrationBuilder.Sql("DELETE FROM \"receipt_source_categories\" WHERE \"IsDefault\" = 1;");
        migrationBuilder.Sql("ALTER TABLE \"receipt_source_categories\" DROP COLUMN \"IsDefault\";");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDefault",
            table: "receipt_source_categories",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }
}
