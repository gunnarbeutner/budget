using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "category_rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayeeText = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PurposeText = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UseRegex = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    AmountSign = table.Column<int>(type: "INTEGER", nullable: false),
                    MinAmountCents = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxAmountCents = table.Column<long>(type: "INTEGER", nullable: true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_category_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_category_rules_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_category_rules_ProjectId_SortOrder",
                table: "category_rules",
                columns: new[] { "ProjectId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_rules");
        }
    }
}
