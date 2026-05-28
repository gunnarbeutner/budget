using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260528093732_AddAccountGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite rebuilds mm_accounts below (to attach the new FK), and the rebuild's
            // intermediate DROP TABLE bumps the deferred-FK-violation counter for every
            // mm_transactions row that referenced mm_accounts. The counter is monotonic — even
            // though the subsequent RENAME restores the references, the COMMIT still trips.
            // Suspend FK enforcement for the migration (must be outside the EF-managed
            // transaction — the PRAGMA is silently ignored inside one). We don't re-enable at
            // the end: EF re-orders the explicit re-enable to fire *before* its
            // auto-appended rebuild, which would re-trigger the bug. The connection-string
            // default (Foreign Keys=True) flips it back on the next freshly-opened connection.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF", suppressTransaction: true);

            // Legacy importer namespaced MoneyMoney's "no parent" sentinel (rowid 0) the same
            // as a real rowid, leaving every root group/category with a dangling FK. Clean them
            // up here so the data is consistent going forward; the updated importer emits NULL
            // for the sentinel directly, so this stays a one-shot cleanup.
            migrationBuilder.Sql(
                "UPDATE mm_category_groups SET group_key = NULL " +
                "WHERE group_key IS NOT NULL AND group_key NOT IN (SELECT Id FROM mm_category_groups)");
            migrationBuilder.Sql(
                "UPDATE mm_categories SET group_key = NULL " +
                "WHERE group_key IS NOT NULL AND group_key NOT IN (SELECT Id FROM mm_category_groups)");

            migrationBuilder.AddColumn<long>(
                name: "group_key",
                table: "mm_accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mm_account_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    group_key = table.Column<long>(type: "INTEGER", nullable: true),
                    sort = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_account_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_account_groups_mm_account_groups_group_key",
                        column: x => x.group_key,
                        principalTable: "mm_account_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mm_accounts_group_key",
                table: "mm_accounts",
                column: "group_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_account_groups_group_key",
                table: "mm_account_groups",
                column: "group_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_account_groups_SourceId",
                table: "mm_account_groups",
                column: "SourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_mm_accounts_mm_account_groups_group_key",
                table: "mm_accounts",
                column: "group_key",
                principalTable: "mm_account_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_mm_accounts_mm_account_groups_group_key",
                table: "mm_accounts");

            migrationBuilder.DropTable(
                name: "mm_account_groups");

            migrationBuilder.DropIndex(
                name: "IX_mm_accounts_group_key",
                table: "mm_accounts");

            migrationBuilder.DropColumn(
                name: "group_key",
                table: "mm_accounts");
        }
    }
}
