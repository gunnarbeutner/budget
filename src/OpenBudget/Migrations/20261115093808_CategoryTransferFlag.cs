using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115093808_CategoryTransferFlag : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "is_transfer",
            table: "category_groups",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "is_transfer",
            table: "categories",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        // The imported transfer group: "Umbuchungen" right under the user's hidden root group (rowid 1 of the id space),
        // and its categories.
        migrationBuilder.Sql("""
            UPDATE category_groups SET is_transfer = 1
            WHERE name = 'Umbuchungen' AND EXISTS (
                SELECT 1 FROM user_data_states s
                WHERE s.UserId = category_groups.OwnerUserId
                  AND category_groups.group_key = CASE WHEN s.IdSpace = 1 THEN 1 ELSE (s.IdSpace << 40) | 1 END);
            """);
        migrationBuilder.Sql("""
            UPDATE categories SET is_transfer = 1
            WHERE group_key IN (SELECT Id FROM category_groups WHERE is_transfer = 1);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "is_transfer",
            table: "category_groups");

        migrationBuilder.DropColumn(
            name: "is_transfer",
            table: "categories");
    }
}
