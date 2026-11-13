using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <summary>
    /// Data belongs to users instead of import sources: every table with a <c>SourceId</c> gets the source owner's id
    /// as <c>OwnerUserId</c>, the per-source state moves to <c>user_data_states</c> (one row per owner; its id space is
    /// the owner's source holding data, so existing ids stay valid and new ones never collide), and <c>import_sources</c>
    /// goes. A source without owner that still holds data stops the migration (CHECK constraint failure); one without
    /// data is dropped. Columns are dropped in place (no table rebuilds); the projects table loses its foreign key to
    /// <c>import_sources</c> by the schema edit SQLite documents for removing a FOREIGN KEY constraint.
    /// </summary>
    public partial class _20261113045640_RemoveSources : Migration
    {
        /// <summary>Tables whose rows carried a source, with the indexes on <c>SourceId</c> they lose and the ones they get.</summary>
        private static readonly (string Table, string[] DropIndexes, string[] CreateIndexes)[] Owned =
        [
            ("transactions",
                ["IX_transactions_SourceId_category_key_timestamp", "IX_transactions_SourceId_local_account_key", "IX_transactions_SourceId_timestamp"],
                [
                    "CREATE INDEX \"IX_transactions_OwnerUserId_category_key_timestamp\" ON \"transactions\" (\"OwnerUserId\", \"category_key\", \"timestamp\");",
                    "CREATE INDEX \"IX_transactions_OwnerUserId_local_account_key\" ON \"transactions\" (\"OwnerUserId\", \"local_account_key\");",
                    "CREATE INDEX \"IX_transactions_OwnerUserId_timestamp\" ON \"transactions\" (\"OwnerUserId\", \"timestamp\");",
                ]),
            ("security_tax_setting", ["IX_security_tax_setting_SourceId_SecurityUuid"],
                ["CREATE UNIQUE INDEX \"IX_security_tax_setting_OwnerUserId_SecurityUuid\" ON \"security_tax_setting\" (\"OwnerUserId\", \"SecurityUuid\");"]),
            ("scheduled_payments", [], []),
            ("removed_import_items", ["IX_removed_import_items_SourceId_Kind_ItemId"],
                ["CREATE UNIQUE INDEX \"IX_removed_import_items_OwnerUserId_Kind_ItemId\" ON \"removed_import_items\" (\"OwnerUserId\", \"Kind\", \"ItemId\");"]),
            ("pp_security_taxonomy", ["IX_pp_security_taxonomy_SourceId_Isin", "IX_pp_security_taxonomy_SourceId_TaxonomyId"],
                [
                    "CREATE INDEX \"IX_pp_security_taxonomy_OwnerUserId_Isin\" ON \"pp_security_taxonomy\" (\"OwnerUserId\", \"Isin\");",
                    "CREATE INDEX \"IX_pp_security_taxonomy_OwnerUserId_TaxonomyId\" ON \"pp_security_taxonomy\" (\"OwnerUserId\", \"TaxonomyId\");",
                ]),
            ("pp_security_link", ["IX_pp_security_link_SourceId_PpSecurityUuid"],
                ["CREATE UNIQUE INDEX \"IX_pp_security_link_OwnerUserId_PpSecurityUuid\" ON \"pp_security_link\" (\"OwnerUserId\", \"PpSecurityUuid\");"]),
            ("pp_security", ["IX_pp_security_SourceId_Isin"],
                ["CREATE INDEX \"IX_pp_security_OwnerUserId_Isin\" ON \"pp_security\" (\"OwnerUserId\", \"Isin\");"]),
            ("pp_portfolio_settings", ["IX_pp_portfolio_settings_SourceId_PpPortfolioUuid"],
                ["CREATE UNIQUE INDEX \"IX_pp_portfolio_settings_OwnerUserId_PpPortfolioUuid\" ON \"pp_portfolio_settings\" (\"OwnerUserId\", \"PpPortfolioUuid\");"]),
            ("pp_portfolio_link", ["IX_pp_portfolio_link_SourceId_PpPortfolioUuid"],
                ["CREATE UNIQUE INDEX \"IX_pp_portfolio_link_OwnerUserId_PpPortfolioUuid\" ON \"pp_portfolio_link\" (\"OwnerUserId\", \"PpPortfolioUuid\");"]),
            ("pp_portfolio", ["IX_pp_portfolio_SourceId"],
                ["CREATE INDEX \"IX_pp_portfolio_OwnerUserId\" ON \"pp_portfolio\" (\"OwnerUserId\");"]),
            ("pp_lot", ["IX_pp_lot_SourceId_Isin"],
                ["CREATE INDEX \"IX_pp_lot_OwnerUserId_Isin\" ON \"pp_lot\" (\"OwnerUserId\", \"Isin\");"]),
            ("portfolio_chart", ["IX_portfolio_chart_SourceId_account_key_timestamp"],
                ["CREATE INDEX \"IX_portfolio_chart_OwnerUserId_account_key_timestamp\" ON \"portfolio_chart\" (\"OwnerUserId\", \"account_key\", \"timestamp\");"]),
            ("holdings", ["IX_holdings_SourceId_account_key"],
                ["CREATE INDEX \"IX_holdings_OwnerUserId_account_key\" ON \"holdings\" (\"OwnerUserId\", \"account_key\");"]),
            ("category_groups", ["IX_category_groups_SourceId"],
                ["CREATE INDEX \"IX_category_groups_OwnerUserId\" ON \"category_groups\" (\"OwnerUserId\");"]),
            ("categories", ["IX_categories_SourceId_group_key"],
                ["CREATE INDEX \"IX_categories_OwnerUserId_group_key\" ON \"categories\" (\"OwnerUserId\", \"group_key\");"]),
            ("accounts", ["IX_accounts_SourceId"],
                ["CREATE INDEX \"IX_accounts_OwnerUserId\" ON \"accounts\" (\"OwnerUserId\");"]),
            ("account_groups", ["IX_account_groups_SourceId"],
                ["CREATE INDEX \"IX_account_groups_OwnerUserId\" ON \"account_groups\" (\"OwnerUserId\");"]),
        ];

        /// <summary>The foreign key clause of <c>projects.SourceId</c> as the earlier migrations wrote it.</summary>
        private const string ProjectsSourceForeignKey =
            "    CONSTRAINT \"FK_projects_money_money_sources_SourceId\" FOREIGN KEY (\"SourceId\") REFERENCES \"import_sources\" (\"Id\") ON DELETE RESTRICT,\n";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A source that holds anything (or has projects) but has no owner cannot be given to anyone: stop loudly.
            var holds = string.Join(" OR ", Owned.Select(o => $"EXISTS (SELECT 1 FROM \"{o.Table}\" x WHERE x.\"SourceId\" = s.\"Id\")")
                .Append("EXISTS (SELECT 1 FROM \"projects\" x WHERE x.\"SourceId\" = s.\"Id\")")
                .Append("EXISTS (SELECT 1 FROM \"bank_statements\" x WHERE x.\"SourceId\" = s.\"Id\")"));
            migrationBuilder.Sql("CREATE TABLE \"__unowned_sources_with_data\" (\"Count\" INTEGER NOT NULL CHECK (\"Count\" = 0));");
            migrationBuilder.Sql($"INSERT INTO \"__unowned_sources_with_data\" SELECT COUNT(*) FROM \"import_sources\" s WHERE s.\"OwnerUserId\" IS NULL AND ({holds});");
            migrationBuilder.Sql("DROP TABLE \"__unowned_sources_with_data\";");

            // One state row per owner. Its id space is the owner's source that holds data (the lowest such id), else
            // their lowest source; LegacyFileKeys lists all their former source ids (their PortfolioPerformance file
            // may be stored under any of them).
            var data = string.Join(" OR ", Owned.Select(o => $"EXISTS (SELECT 1 FROM \"{o.Table}\" x WHERE x.\"SourceId\" = p.\"Id\")"));
            migrationBuilder.Sql("""
                CREATE TABLE "user_data_states" (
                    "UserId" TEXT NOT NULL CONSTRAINT "PK_user_data_states" PRIMARY KEY,
                    "IdSpace" INTEGER NOT NULL,
                    "LastUploadAt" TEXT NULL,
                    "LastUploadSizeBytes" INTEGER NULL,
                    "StructureTakenOverAt" TEXT NULL,
                    "MoneyMoneyImportFinishedAt" TEXT NULL,
                    "LegacyFileKeys" TEXT NULL,
                    "GetStartedCompletedAt" TEXT NULL
                );
                """);
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_user_data_states_IdSpace\" ON \"user_data_states\" (\"IdSpace\");");
            // Users with data are set up already, so Get started counts as completed for them.
            migrationBuilder.Sql($"""
                INSERT INTO "user_data_states" ("UserId", "IdSpace", "LastUploadAt", "LastUploadSizeBytes", "StructureTakenOverAt", "MoneyMoneyImportFinishedAt", "LegacyFileKeys", "GetStartedCompletedAt")
                SELECT s."OwnerUserId",
                       (SELECT p."Id" FROM "import_sources" p WHERE p."OwnerUserId" = s."OwnerUserId" ORDER BY ({data}) DESC, p."Id" LIMIT 1),
                       MAX(s."LastUploadAt"), MAX(s."LastUploadSizeBytes"), MAX(s."StructureTakenOverAt"), MAX(s."MoneyMoneyImportFinishedAt"),
                       group_concat(s."Id", ','), strftime('%Y-%m-%d %H:%M:%f', 'now')
                FROM "import_sources" s
                WHERE s."OwnerUserId" IS NOT NULL
                GROUP BY s."OwnerUserId";
                """);

            // Every row gets its source's owner, then loses the source.
            foreach (var (table, dropIndexes, createIndexes) in Owned)
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD COLUMN \"OwnerUserId\" TEXT NOT NULL DEFAULT '';");
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"OwnerUserId\" = COALESCE((SELECT s.\"OwnerUserId\" FROM \"import_sources\" s WHERE s.\"Id\" = \"{table}\".\"SourceId\"), '');");
                foreach (var index in dropIndexes) migrationBuilder.Sql($"DROP INDEX \"{index}\";");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" DROP COLUMN \"SourceId\";");
                foreach (var create in createIndexes) migrationBuilder.Sql(create);
            }
            migrationBuilder.Sql("ALTER TABLE \"bank_statements\" DROP COLUMN \"SourceId\";");
            migrationBuilder.Sql("ALTER TABLE \"cloud_file_links\" DROP COLUMN \"TargetSourceName\";");

            // projects.SourceId is part of a foreign key, which DROP COLUMN refuses: remove the constraint from the stored
            // schema first (no data changes), reload the schema, then drop the column. A schema that does not contain the
            // expected clause makes DROP COLUMN fail, so nothing is changed silently.
            migrationBuilder.Sql("PRAGMA writable_schema = ON;");
            migrationBuilder.Sql($"UPDATE sqlite_schema SET sql = replace(sql, '{ProjectsSourceForeignKey.Replace("'", "''", StringComparison.Ordinal)}', '') WHERE type = 'table' AND name = 'projects';");
            migrationBuilder.Sql("PRAGMA writable_schema = RESET;");
            migrationBuilder.Sql("DROP INDEX \"IX_projects_SourceId\";");
            migrationBuilder.Sql("ALTER TABLE \"projects\" DROP COLUMN \"SourceId\";");

            migrationBuilder.Sql("DROP TABLE \"import_sources\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Data that belongs to users cannot be split back into import sources.");
    }
}
