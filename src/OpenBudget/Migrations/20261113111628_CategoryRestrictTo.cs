using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261113111628_CategoryRestrictTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "restrict_account_group_id",
                table: "categories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "restrict_account_id",
                table: "categories",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "restrict_account_group_id",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "restrict_account_id",
                table: "categories");
        }
    }
}
