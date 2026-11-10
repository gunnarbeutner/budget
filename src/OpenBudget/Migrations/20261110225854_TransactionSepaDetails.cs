using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class TransactionSepaDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "creditor_id",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "end_to_end_reference",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mandate_reference",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primanota",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "return_reason",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_code",
                table: "transactions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "creditor_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "end_to_end_reference",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "mandate_reference",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "primanota",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "return_reason",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "transaction_code",
                table: "transactions");
        }
    }
}
