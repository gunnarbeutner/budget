using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class MultiSourceMoneyMoney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default to SourceId=1 so existing projects bind to the legacy/default source seeded
            // below. Without this, the FK to money_money_sources at the bottom would reject
            // rows that pointed at the non-existent source id 0.
            migrationBuilder.AddColumn<int>(
                name: "SourceId",
                table: "projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "mm_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    iban = table.Column<string>(type: "TEXT", nullable: true),
                    currency = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<long>(type: "INTEGER", nullable: true),
                    portfolio = table.Column<long>(type: "INTEGER", nullable: true),
                    balances = table.Column<string>(type: "TEXT", nullable: true),
                    sort = table.Column<long>(type: "INTEGER", nullable: true),
                    refresh_tsp = table.Column<long>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mm_category_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    group_key = table.Column<long>(type: "INTEGER", nullable: true),
                    sort = table.Column<long>(type: "INTEGER", nullable: true),
                    color = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_category_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_category_groups_mm_category_groups_group_key",
                        column: x => x.group_key,
                        principalTable: "mm_category_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "money_money_sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUploadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUploadSizeBytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_money_money_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mm_categories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<long>(type: "INTEGER", nullable: true),
                    color = table.Column<string>(type: "TEXT", nullable: true),
                    rules = table.Column<string>(type: "TEXT", nullable: true),
                    group_key = table.Column<long>(type: "INTEGER", nullable: true),
                    sort = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_categories_mm_category_groups_group_key",
                        column: x => x.group_key,
                        principalTable: "mm_category_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mm_transactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    local_account_key = table.Column<long>(type: "INTEGER", nullable: true),
                    amount = table.Column<double>(type: "REAL", nullable: false),
                    currency = table.Column<string>(type: "TEXT", nullable: true),
                    timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    comment = table.Column<string>(type: "TEXT", nullable: true),
                    category_key = table.Column<long>(type: "INTEGER", nullable: true),
                    checkmark = table.Column<long>(type: "INTEGER", nullable: true),
                    splitted = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mm_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mm_transactions_mm_accounts_local_account_key",
                        column: x => x.local_account_key,
                        principalTable: "mm_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mm_transactions_mm_categories_category_key",
                        column: x => x.category_key,
                        principalTable: "mm_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "money_money_sources",
                columns: new[] { "Id", "CreatedAt", "LastUploadAt", "LastUploadSizeBytes", "Name" },
                values: new object[] { 1, new DateTime(2020, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "default" });

            migrationBuilder.CreateIndex(
                name: "IX_projects_SourceId",
                table: "projects",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_mm_accounts_SourceId",
                table: "mm_accounts",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_mm_categories_group_key",
                table: "mm_categories",
                column: "group_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_categories_SourceId_group_key",
                table: "mm_categories",
                columns: new[] { "SourceId", "group_key" });

            migrationBuilder.CreateIndex(
                name: "IX_mm_category_groups_group_key",
                table: "mm_category_groups",
                column: "group_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_category_groups_SourceId",
                table: "mm_category_groups",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_mm_transactions_category_key",
                table: "mm_transactions",
                column: "category_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_transactions_local_account_key",
                table: "mm_transactions",
                column: "local_account_key");

            migrationBuilder.CreateIndex(
                name: "IX_mm_transactions_SourceId_category_key_timestamp",
                table: "mm_transactions",
                columns: new[] { "SourceId", "category_key", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_mm_transactions_SourceId_local_account_key",
                table: "mm_transactions",
                columns: new[] { "SourceId", "local_account_key" });

            migrationBuilder.CreateIndex(
                name: "IX_mm_transactions_SourceId_timestamp",
                table: "mm_transactions",
                columns: new[] { "SourceId", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_money_money_sources_Name",
                table: "money_money_sources",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_projects_money_money_sources_SourceId",
                table: "projects",
                column: "SourceId",
                principalTable: "money_money_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_projects_money_money_sources_SourceId",
                table: "projects");

            migrationBuilder.DropTable(
                name: "mm_transactions");

            migrationBuilder.DropTable(
                name: "money_money_sources");

            migrationBuilder.DropTable(
                name: "mm_accounts");

            migrationBuilder.DropTable(
                name: "mm_categories");

            migrationBuilder.DropTable(
                name: "mm_category_groups");

            migrationBuilder.DropIndex(
                name: "IX_projects_SourceId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "projects");
        }
    }
}
