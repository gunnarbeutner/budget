using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Seed existing rows so the newest project ends up first (matches the prior
            // CreatedAt DESC ordering). Ties broken by descending Id for stability.
            migrationBuilder.Sql(@"
                UPDATE projects SET SortOrder = (
                    SELECT COUNT(*) FROM projects p2
                    WHERE p2.CreatedAt > projects.CreatedAt
                       OR (p2.CreatedAt = projects.CreatedAt AND p2.Id > projects.Id)
                )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "projects");
        }
    }
}
