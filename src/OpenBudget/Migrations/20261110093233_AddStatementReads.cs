using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddStatementReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement_reads",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StatementId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statement_reads", x => new { x.UserId, x.StatementId });
                    table.ForeignKey(
                        name: "FK_statement_reads_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_statement_reads_bank_statements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "bank_statements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_statement_reads_StatementId",
                table: "statement_reads",
                column: "StatementId");

            // Existing users start with nothing unread: every statement already downloaded counts as read.
            migrationBuilder.Sql(
                "INSERT INTO statement_reads (UserId, StatementId, ReadAt) "
                + "SELECT u.Id, s.Id, strftime('%Y-%m-%d %H:%M:%S', 'now') FROM AspNetUsers u CROSS JOIN bank_statements s;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statement_reads");
        }
    }
}
