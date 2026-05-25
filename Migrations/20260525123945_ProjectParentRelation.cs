using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class ProjectParentRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentProjectId",
                table: "projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_ParentProjectId",
                table: "projects",
                column: "ParentProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_projects_projects_ParentProjectId",
                table: "projects",
                column: "ParentProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_projects_projects_ParentProjectId",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_projects_ParentProjectId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "ParentProjectId",
                table: "projects");
        }
    }
}
