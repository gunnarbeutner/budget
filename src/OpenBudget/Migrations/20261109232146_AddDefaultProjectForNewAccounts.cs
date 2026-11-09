using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultProjectForNewAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DefaultClearedNotice",
                table: "projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultForNewAccounts",
                table: "projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_projects_OwnerUserId_Default",
                table: "projects",
                column: "OwnerUserId",
                unique: true,
                filter: "IsDefaultForNewAccounts = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_projects_OwnerUserId_Default",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "DefaultClearedNotice",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "IsDefaultForNewAccounts",
                table: "projects");
        }
    }
}
