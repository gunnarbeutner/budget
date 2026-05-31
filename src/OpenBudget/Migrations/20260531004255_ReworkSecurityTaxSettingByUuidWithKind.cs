using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class _20260531004255_ReworkSecurityTaxSettingByUuidWithKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Isin",
                table: "security_tax_setting",
                newName: "SecurityUuid");

            migrationBuilder.RenameIndex(
                name: "IX_security_tax_setting_SourceId_Isin",
                table: "security_tax_setting",
                newName: "IX_security_tax_setting_SourceId_SecurityUuid");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "security_tax_setting",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "security_tax_setting");

            migrationBuilder.RenameColumn(
                name: "SecurityUuid",
                table: "security_tax_setting",
                newName: "Isin");

            migrationBuilder.RenameIndex(
                name: "IX_security_tax_setting_SourceId_SecurityUuid",
                table: "security_tax_setting",
                newName: "IX_security_tax_setting_SourceId_Isin");
        }
    }
}
