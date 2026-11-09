using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261109170804_AddAccountProviderBookedAndSplitLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "booked",
                table: "transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "split_original_amount",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "split_parent_id",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "accounts",
                type: "TEXT",
                nullable: false,
                defaultValue: "moneymoney");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "booked",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "split_original_amount",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "split_parent_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "accounts");
        }
    }
}
