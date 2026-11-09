using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <summary>
    /// Brand-neutral table names: the mm_* tables lose their MoneyMoney prefix and
    /// money_money_sources becomes import_sources. Pure renames (no drop/create) so existing
    /// rows survive; indexes are renamed to match EF's default naming for the new table names.
    /// </summary>
    public partial class RenameToOpenBudgetNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "mm_accounts",
                newName: "accounts");

            migrationBuilder.RenameTable(
                name: "mm_account_groups",
                newName: "account_groups");

            migrationBuilder.RenameTable(
                name: "mm_categories",
                newName: "categories");

            migrationBuilder.RenameTable(
                name: "mm_category_groups",
                newName: "category_groups");

            migrationBuilder.RenameTable(
                name: "mm_transactions",
                newName: "transactions");

            migrationBuilder.RenameTable(
                name: "mm_portfolio_chart",
                newName: "portfolio_chart");

            migrationBuilder.RenameTable(
                name: "mm_securities",
                newName: "holdings");

            migrationBuilder.RenameTable(
                name: "money_money_sources",
                newName: "import_sources");

            migrationBuilder.RenameIndex(
                name: "IX_mm_accounts_SourceId",
                table: "accounts",
                newName: "IX_accounts_SourceId");

            migrationBuilder.RenameIndex(
                name: "IX_mm_accounts_group_key",
                table: "accounts",
                newName: "IX_accounts_group_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_account_groups_SourceId",
                table: "account_groups",
                newName: "IX_account_groups_SourceId");

            migrationBuilder.RenameIndex(
                name: "IX_mm_account_groups_group_key",
                table: "account_groups",
                newName: "IX_account_groups_group_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_categories_SourceId_group_key",
                table: "categories",
                newName: "IX_categories_SourceId_group_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_categories_group_key",
                table: "categories",
                newName: "IX_categories_group_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_category_groups_SourceId",
                table: "category_groups",
                newName: "IX_category_groups_SourceId");

            migrationBuilder.RenameIndex(
                name: "IX_mm_category_groups_group_key",
                table: "category_groups",
                newName: "IX_category_groups_group_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_transactions_SourceId_category_key_timestamp",
                table: "transactions",
                newName: "IX_transactions_SourceId_category_key_timestamp");

            migrationBuilder.RenameIndex(
                name: "IX_mm_transactions_SourceId_local_account_key",
                table: "transactions",
                newName: "IX_transactions_SourceId_local_account_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_transactions_SourceId_timestamp",
                table: "transactions",
                newName: "IX_transactions_SourceId_timestamp");

            migrationBuilder.RenameIndex(
                name: "IX_mm_transactions_category_key",
                table: "transactions",
                newName: "IX_transactions_category_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_transactions_local_account_key",
                table: "transactions",
                newName: "IX_transactions_local_account_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_portfolio_chart_SourceId_account_key_timestamp",
                table: "portfolio_chart",
                newName: "IX_portfolio_chart_SourceId_account_key_timestamp");

            migrationBuilder.RenameIndex(
                name: "IX_mm_portfolio_chart_account_key",
                table: "portfolio_chart",
                newName: "IX_portfolio_chart_account_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_securities_SourceId_account_key",
                table: "holdings",
                newName: "IX_holdings_SourceId_account_key");

            migrationBuilder.RenameIndex(
                name: "IX_mm_securities_account_key",
                table: "holdings",
                newName: "IX_holdings_account_key");

            migrationBuilder.RenameIndex(
                name: "IX_money_money_sources_Name",
                table: "import_sources",
                newName: "IX_import_sources_Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "accounts",
                newName: "mm_accounts");

            migrationBuilder.RenameTable(
                name: "account_groups",
                newName: "mm_account_groups");

            migrationBuilder.RenameTable(
                name: "categories",
                newName: "mm_categories");

            migrationBuilder.RenameTable(
                name: "category_groups",
                newName: "mm_category_groups");

            migrationBuilder.RenameTable(
                name: "transactions",
                newName: "mm_transactions");

            migrationBuilder.RenameTable(
                name: "portfolio_chart",
                newName: "mm_portfolio_chart");

            migrationBuilder.RenameTable(
                name: "holdings",
                newName: "mm_securities");

            migrationBuilder.RenameTable(
                name: "import_sources",
                newName: "money_money_sources");

            // SQLite cannot run RenameIndex on the way down, so restore the old index names by hand.
            migrationBuilder.Sql("DROP INDEX \"IX_account_groups_SourceId\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_account_groups_SourceId\" ON \"mm_account_groups\" (\"SourceId\");");
            migrationBuilder.Sql("DROP INDEX \"IX_account_groups_group_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_account_groups_group_key\" ON \"mm_account_groups\" (\"group_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_accounts_SourceId\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_accounts_SourceId\" ON \"mm_accounts\" (\"SourceId\");");
            migrationBuilder.Sql("DROP INDEX \"IX_accounts_group_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_accounts_group_key\" ON \"mm_accounts\" (\"group_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_categories_SourceId_group_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_categories_SourceId_group_key\" ON \"mm_categories\" (\"SourceId\", \"group_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_categories_group_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_categories_group_key\" ON \"mm_categories\" (\"group_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_category_groups_SourceId\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_category_groups_SourceId\" ON \"mm_category_groups\" (\"SourceId\");");
            migrationBuilder.Sql("DROP INDEX \"IX_category_groups_group_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_category_groups_group_key\" ON \"mm_category_groups\" (\"group_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_portfolio_chart_SourceId_account_key_timestamp\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_portfolio_chart_SourceId_account_key_timestamp\" ON \"mm_portfolio_chart\" (\"SourceId\", \"account_key\", \"timestamp\");");
            migrationBuilder.Sql("DROP INDEX \"IX_portfolio_chart_account_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_portfolio_chart_account_key\" ON \"mm_portfolio_chart\" (\"account_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_holdings_SourceId_account_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_securities_SourceId_account_key\" ON \"mm_securities\" (\"SourceId\", \"account_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_holdings_account_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_securities_account_key\" ON \"mm_securities\" (\"account_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_transactions_SourceId_category_key_timestamp\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_transactions_SourceId_category_key_timestamp\" ON \"mm_transactions\" (\"SourceId\", \"category_key\", \"timestamp\");");
            migrationBuilder.Sql("DROP INDEX \"IX_transactions_SourceId_local_account_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_transactions_SourceId_local_account_key\" ON \"mm_transactions\" (\"SourceId\", \"local_account_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_transactions_SourceId_timestamp\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_transactions_SourceId_timestamp\" ON \"mm_transactions\" (\"SourceId\", \"timestamp\");");
            migrationBuilder.Sql("DROP INDEX \"IX_transactions_category_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_transactions_category_key\" ON \"mm_transactions\" (\"category_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_transactions_local_account_key\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_mm_transactions_local_account_key\" ON \"mm_transactions\" (\"local_account_key\");");
            migrationBuilder.Sql("DROP INDEX \"IX_import_sources_Name\";");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_money_money_sources_Name\" ON \"money_money_sources\" (\"Name\");");
        }
    }
}
