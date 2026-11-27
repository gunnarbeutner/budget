using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261127072450_ApiTokens : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_tokens",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_tokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_tokens_AspNetUsers_OwnerUserId",
                    column: x => x.OwnerUserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_OwnerUserId",
            table: "api_tokens",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_TokenHash",
            table: "api_tokens",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "api_tokens");
    }
}
