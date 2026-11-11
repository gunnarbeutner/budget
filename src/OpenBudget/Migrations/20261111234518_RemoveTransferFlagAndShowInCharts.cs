using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261111234518_RemoveTransferFlagAndShowInCharts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_transfer",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "transfer_peer_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "show_in_charts",
                table: "categories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_transfer",
                table: "transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "transfer_peer_id",
                table: "transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_in_charts",
                table: "categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }
    }
}
