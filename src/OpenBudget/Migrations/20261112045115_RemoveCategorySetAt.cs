using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261112045115_RemoveCategorySetAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite drops the column in place. EF's DropColumn rebuilds the table instead,
            // which fails at commit with deferred foreign key violations on databases with data.
            migrationBuilder.Sql("ALTER TABLE \"transactions\" DROP COLUMN \"category_set_at\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "category_set_at",
                table: "transactions",
                type: "INTEGER",
                nullable: true);
        }
    }
}
