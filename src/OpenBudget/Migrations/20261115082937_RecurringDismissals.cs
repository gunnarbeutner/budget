using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261115082937_RecurringDismissals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "recurring_dismissals",
            columns: table => new
            {
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                SeriesKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recurring_dismissals", x => new { x.OwnerUserId, x.SeriesKey });
                table.ForeignKey(
                    name: "FK_recurring_dismissals_AspNetUsers_OwnerUserId",
                    column: x => x.OwnerUserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "recurring_dismissals");
    }
}
