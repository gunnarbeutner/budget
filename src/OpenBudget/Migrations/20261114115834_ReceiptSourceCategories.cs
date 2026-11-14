using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114115834_ReceiptSourceCategories : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "receipt_source_categories",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Keywords = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_source_categories", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_receipt_source_categories_OwnerUserId_Source_CategoryId",
            table: "receipt_source_categories",
            columns: new[] { "OwnerUserId", "Source", "CategoryId" },
            unique: true);

        // The single non-food category becomes an allowed category of the mailbox and of Lidl Plus.
        foreach (var source in new[] { "mail", "lidl" })
            migrationBuilder.Sql($"""
                INSERT INTO receipt_source_categories (OwnerUserId, Source, CategoryId, IsDefault, Description, Keywords)
                SELECT s.OwnerUserId, '{source}', s.NonFoodCategoryId, 0, 'Drogerie, Haushalt, Non-Food', NULL
                FROM receipt_settings s
                WHERE s.NonFoodCategoryId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM categories c WHERE c.Id = s.NonFoodCategoryId AND c.OwnerUserId = s.OwnerUserId);
                """);
        migrationBuilder.Sql("ALTER TABLE receipt_settings DROP COLUMN NonFoodCategoryId;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "receipt_source_categories");

        migrationBuilder.AddColumn<long>(
            name: "NonFoodCategoryId",
            table: "receipt_settings",
            type: "INTEGER",
            nullable: true);
    }
}
