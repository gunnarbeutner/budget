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
            // SQLite drops these columns in place. EF's DropColumn rebuilds both tables
            // instead, and dropping the old "categories" while "transactions" references it
            // counts a deferred foreign key violation per categorised transaction, so the
            // commit fails on any database that has categorised transactions.
            migrationBuilder.Sql("ALTER TABLE \"transactions\" DROP COLUMN \"is_transfer\";");
            migrationBuilder.Sql("ALTER TABLE \"transactions\" DROP COLUMN \"transfer_peer_id\";");
            migrationBuilder.Sql("ALTER TABLE \"categories\" DROP COLUMN \"show_in_charts\";");
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
