using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class AddBankConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bank_logins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ConnectorType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EncryptedCredentials = table.Column<string>(type: "TEXT", nullable: true),
                    EncryptedSession = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshInterval = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CredentialsInvalid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_logins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_statements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    Identifier = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_login_accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BankLoginId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Iban = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    LinkedAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedByConnector = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncFloorUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatementsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatementsInterval = table.Column<int>(type: "INTEGER", nullable: false),
                    LastStatementsAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatementAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_login_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_login_accounts_bank_logins_BankLoginId",
                        column: x => x.BankLoginId,
                        principalTable: "bank_logins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_login_accounts_BankLoginId_RemoteId",
                table: "bank_login_accounts",
                columns: new[] { "BankLoginId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_logins_OwnerUserId",
                table: "bank_logins",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statements_AccountId",
                table: "bank_statements",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statements_BankLoginId_Identifier",
                table: "bank_statements",
                columns: new[] { "BankLoginId", "Identifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_login_accounts");

            migrationBuilder.DropTable(
                name: "bank_statements");

            migrationBuilder.DropTable(
                name: "bank_logins");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "transactions");
        }
    }
}
