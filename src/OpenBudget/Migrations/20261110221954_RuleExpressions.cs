using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <summary>
    /// Rules become search expressions: the old payee / purpose / regex / account / amount columns are converted into an
    /// equivalent expression (the shape the rule editor's builder composes) and dropped.
    /// </summary>
    public partial class RuleExpressions : Migration
    {
        private static string Text(string column) =>
            $"CASE WHEN UseRegex THEN '/' || replace(trim({column}), '/', '\\/') || '/' ELSE '\"' || replace(trim({column}), '\"', '') || '\"' END";

        private static string Bound(string op, string column, bool negate) =>
            $"CASE WHEN {column} IS NULL THEN '' ELSE ' AND amount{op}' || printf('%.2f', {(negate ? "-" : "")}{column} / 100.0) END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "category_rules",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImportOrigin",
                table: "category_rules",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedExpression",
                table: "category_rules",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Dismissed",
                table: "category_rules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            var payee = $"CASE WHEN PayeeText IS NOT NULL AND trim(PayeeText) <> '' THEN ' AND name:' || {Text("PayeeText")} ELSE '' END";
            var purpose = $"CASE WHEN PurposeText IS NOT NULL AND trim(PurposeText) <> '' THEN ' AND (purpose:' || {Text("PurposeText")} || ' OR booking_text:' || {Text("PurposeText")} || ')' ELSE '' END";
            var account = """
                CASE WHEN AccountId IS NULL THEN '' ELSE ' AND ' || COALESCE(
                    (SELECT 'local_account="' || upper(replace(a.Iban, ' ', '')) || '"' FROM accounts a WHERE a.Id = category_rules.AccountId AND a.Iban IS NOT NULL AND trim(a.Iban) <> ''),
                    'account_id=' || AccountId) END
                """;
            var anyRange = $"""
                CASE WHEN MinAmountCents IS NULL AND MaxAmountCents IS NULL THEN '' ELSE
                    ' AND (' || substr({Bound(">=", "MinAmountCents", false)} || {Bound("<=", "MaxAmountCents", false)}, 6)
                    || ' OR ' || substr({Bound("<=", "MinAmountCents", true)} || {Bound(">=", "MaxAmountCents", true)}, 6) || ')' END
                """;
            var amount = $"""
                CASE AmountSign
                    WHEN 1 THEN ' AND amount<0' || {Bound("<=", "MinAmountCents", true)} || {Bound(">=", "MaxAmountCents", true)}
                    WHEN 2 THEN ' AND amount>0' || {Bound(">=", "MinAmountCents", false)} || {Bound("<=", "MaxAmountCents", false)}
                    ELSE {anyRange} END
                """;
            migrationBuilder.Sql($"UPDATE category_rules SET Expression = substr({payee} || {purpose} || {account} || {amount}, 6);");

            migrationBuilder.DropColumn(name: "AccountId", table: "category_rules");
            migrationBuilder.DropColumn(name: "AmountSign", table: "category_rules");
            migrationBuilder.DropColumn(name: "MaxAmountCents", table: "category_rules");
            migrationBuilder.DropColumn(name: "MinAmountCents", table: "category_rules");
            migrationBuilder.DropColumn(name: "PayeeText", table: "category_rules");
            migrationBuilder.DropColumn(name: "PurposeText", table: "category_rules");
            migrationBuilder.DropColumn(name: "UseRegex", table: "category_rules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy: expressions do not convert back; rules come back without conditions (they never match).
            migrationBuilder.DropColumn(name: "Expression", table: "category_rules");
            migrationBuilder.DropColumn(name: "ImportOrigin", table: "category_rules");
            migrationBuilder.DropColumn(name: "ImportedExpression", table: "category_rules");
            migrationBuilder.DropColumn(name: "Dismissed", table: "category_rules");
            migrationBuilder.AddColumn<bool>(name: "UseRegex", table: "category_rules", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<long>(name: "AccountId", table: "category_rules", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<int>(name: "AmountSign", table: "category_rules", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<long>(name: "MaxAmountCents", table: "category_rules", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "MinAmountCents", table: "category_rules", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PayeeText", table: "category_rules", type: "TEXT", maxLength: 500, nullable: true);
            migrationBuilder.AddColumn<string>(name: "PurposeText", table: "category_rules", type: "TEXT", maxLength: 500, nullable: true);
        }
    }
}
