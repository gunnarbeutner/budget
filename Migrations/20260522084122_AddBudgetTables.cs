using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budgets_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "monthly_budget_entries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BudgetId = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Month1 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month2 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month3 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month4 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month5 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month6 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month7 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month8 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month9 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month10 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month11 = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Month12 = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_budget_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monthly_budget_entries_budgets_BudgetId",
                        column: x => x.BudgetId,
                        principalTable: "budgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budgets_ProjectId",
                table: "budgets",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_monthly_budget_entries_BudgetId",
                table: "monthly_budget_entries",
                column: "BudgetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monthly_budget_entries");

            migrationBuilder.DropTable(
                name: "budgets");
        }
    }
}
