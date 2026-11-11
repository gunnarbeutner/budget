using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20261111094048_CloudFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_connections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NeedsReconnect = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cloud_file_links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    DriveId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    TargetSourceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastCTag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastETag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RemoteModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_file_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cloud_file_links_cloud_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "cloud_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cloud_connections_OwnerUserId_Provider",
                table: "cloud_connections",
                columns: new[] { "OwnerUserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_file_links_ConnectionId_DriveId_ItemId",
                table: "cloud_file_links",
                columns: new[] { "ConnectionId", "DriveId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_file_links_OwnerUserId",
                table: "cloud_file_links",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_file_links");

            migrationBuilder.DropTable(
                name: "cloud_connections");
        }
    }
}
