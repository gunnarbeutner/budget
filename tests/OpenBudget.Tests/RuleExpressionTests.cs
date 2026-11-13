using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The rule language: tokenizer, precedence, quoting, fields, amounts, and the editor's builder round trip.</summary>
public sealed class RuleExpressionTests
{
    private static readonly RuleSubject Market = new(
        AccountId: 7, Amount: -1250, Name: "EXAMPLE MARKET 042", Purpose: "Card payment EREF+123 MREF+M-0001 CRED+DE00ZZZ0000001", BookingText: "Lastschrift",
        Comment: "weekly shopping", Note: null, CounterpartyIban: "DE00 1234 5678 9000 0000 01", LocalAccount: "DE00999988887777666655", Currency: "EUR", BankReference: "REF-77");

    private static bool M(string expression, RuleSubject? subject = null) => RuleExpression.Matches(expression, subject ?? Market);

    [Theory]
    [InlineData("market", true)]
    [InlineData("MARKET", true)]
    [InlineData("\"example market\"", true)]
    [InlineData("\"market example\"", false)]
    [InlineData("example 042", true)]                // implicit AND
    [InlineData("example bakery", false)]
    [InlineData("example AND bakery", false)]
    [InlineData("bakery OR market", true)]
    [InlineData("NOT bakery", true)]
    [InlineData("NOT market", false)]
    [InlineData("shopping", true)]                    // default fields include the comment
    [InlineData("lastschrift", true)]                 // ... and the booking text
    [InlineData("exa*ket", false)]                    // wildcard stays inside a word
    [InlineData("mar*", true)]
    [InlineData("m?rket", true)]
    [InlineData("/MARKET [0-9]{3}/", true)]
    [InlineData("/^market/", false)]
    public void Text_terms(string expression, bool expected) => Assert.Equal(expected, M(expression));

    [Theory]
    [InlineData("bakery OR market AND example", true)]       // AND binds tighter: bakery OR (market AND example)
    [InlineData("bakery OR market AND sample", false)]
    [InlineData("(bakery OR market) AND sample", false)]
    [InlineData("(bakery OR market) AND example", true)]
    [InlineData("NOT bakery AND market", true)]                // NOT binds tighter than AND
    [InlineData("NOT (bakery OR market)", false)]
    [InlineData("NOT bakery OR bakery", true)]
    [InlineData("((market))", true)]
    public void Precedence_and_grouping(string expression, bool expected) => Assert.Equal(expected, M(expression));

    [Theory]
    [InlineData("name:market", true)]
    [InlineData("name:\"example market\"", true)]
    [InlineData("name=\"example market\"", true)]
    [InlineData("name:shopping", false)]
    [InlineData("name!=bakery", true)]
    [InlineData("name!=market", false)]
    [InlineData("purpose:\"card payment\"", true)]
    [InlineData("purpose:market", false)]
    [InlineData("comment:weekly", true)]
    [InlineData("booking_text:lastschrift", true)]
    [InlineData("currency:EUR", true)]
    [InlineData("reference:REF-77", true)]
    [InlineData("mandate:M-0001", true)]
    [InlineData("creditor_id=\"DE00ZZZ0000001\"", true)]
    [InlineData("remote_account=\"DE00123456789000000001\"", true)]  // spaces ignored
    [InlineData("remote_account:9000", true)]
    [InlineData("local_account=\"DE00 9999 8888 7777 6666 55\"", true)]
    [InlineData("local_account=\"DE11\"", false)]
    [InlineData("account_id=7", true)]
    [InlineData("account_id=8", false)]
    [InlineData("NAME:market", true)]
    [InlineData("name:/^example/", true)]
    public void Fields(string expression, bool expected) => Assert.Equal(expected, M(expression));

    [Theory]
    [InlineData("amount<0", true)]
    [InlineData("amount>0,00", false)]
    [InlineData("amount=-12,50", true)]
    [InlineData("amount==-12.50", true)]
    [InlineData("amount:-12.5", true)]
    [InlineData("amount!=-12.50", false)]
    [InlineData("amount>=-12.50", true)]
    [InlineData("amount<=-12.51", false)]
    [InlineData("amount>-20 AND amount<-10", true)]
    [InlineData("amount<-1.000,00", false)]
    public void Amounts(string expression, bool expected) => Assert.Equal(expected, M(expression));

    [Fact]
    public void Umlauts_match_their_transliteration()
    {
        var s = Market with { Name = "Bäckerei Müller", Purpose = "Strasse" };
        Assert.True(M("baeckerei", s));
        Assert.True(M("\"Bäckerei Mueller\"", s));
        Assert.True(M("straße", s));
    }

    [Fact]
    public void Unknown_fields_never_match_and_are_reported_once()
    {
        var reported = new List<string>();
        RuleExpression.UnknownFieldLog = reported.Add;
        try
        {
            Assert.False(M("category=\"Food\" OR nothinghere"));
            Assert.True(M("NOT category=\"Food\""));
            Assert.Single(reported, "category");
        }
        finally
        {
            RuleExpression.UnknownFieldLog = null;
        }
        // Without a quoted value an unknown "x:y" is just a word.
        Assert.False(M("ref:abc"));
        Assert.True(M("EREF+123"));
        Assert.True(M("xyz/123 OR market"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(market")]
    [InlineData("market)")]
    [InlineData("\"unclosed")]
    [InlineData("market AND")]
    [InlineData("OR market")]
    [InlineData("NOT")]
    [InlineData("amount>abc")]
    [InlineData("amount>")]
    [InlineData("name:")]
    [InlineData("name<5")]
    [InlineData("name:/([/")]
    public void Invalid_expressions_do_not_parse_and_never_match(string expression)
    {
        Assert.False(RuleExpression.IsValid(expression));
        Assert.False(M(expression));
    }

    [Fact]
    public void Lowercase_operators_are_words()
    {
        Assert.False(M("bakery or market")); // implicit AND of three words
        Assert.True(M("market and example", Market with { Name = "market and example" }));
    }

    [Theory]
    [InlineData("account_group=3", true)]
    [InlineData("account_group=1", true)]
    [InlineData("account_group=4", false)]
    [InlineData("account_group!=4", true)]
    [InlineData("ACCOUNT_GROUP:3", true)]
    public void Account_group_matches_the_accounts_groups_and_their_parents(string expression, bool expected) =>
        Assert.Equal(expected, M(expression, Market with { AccountGroups = new HashSet<long> { 1, 3 } }));

    [Fact]
    public void Account_group_needs_a_number_and_prints_canonically()
    {
        Assert.False(M("account_group=3"));
        Assert.False(RuleExpression.IsValid("account_group=abc"));
        Assert.False(RuleExpression.IsValid("account_group<3"));
        Assert.Equal("account_group=3", RuleExpression.TryParse("account_group:3", out _)!.Print());
    }

    // ---- builder ----

    private static readonly RuleAccount[] Accounts = [new(7, "DE00 9999 8888 7777 6666 55"), new(8, null)];

    public static TheoryData<RuleParts, string> Shapes => new()
    {
        { new RuleParts { Payee = "Example Market" }, "name:\"Example Market\"" },
        { new RuleParts { Payee = "^example", Regex = true }, "name:/^example/" },
        { new RuleParts { Purpose = "monthly fee" }, "purpose:\"monthly fee\" OR booking_text:\"monthly fee\"" },
        { new RuleParts { Payee = "a", Purpose = "b" }, "name:\"a\" AND (purpose:\"b\" OR booking_text:\"b\")" },
        { new RuleParts { Payee = "a", AccountId = 7 }, "name:\"a\" AND local_account=\"DE00999988887777666655\"" },
        { new RuleParts { Payee = "a", AccountId = 8 }, "name:\"a\" AND account_id=8" },
        { new RuleParts { Payee = "a", AccountGroupId = 3 }, "name:\"a\" AND account_group=3" },
        { new RuleParts { Payee = "a", Sign = RuleAmountSign.Expense, Min = 5, Max = 50 }, "name:\"a\" AND amount<0 AND amount<=-5 AND amount>=-50" },
        { new RuleParts { Payee = "a", Sign = RuleAmountSign.Income, Min = 5 }, "name:\"a\" AND amount>0 AND amount>=5" },
        { new RuleParts { Payee = "a", Min = 5, Max = 50 }, "name:\"a\" AND (amount>=5 AND amount<=50 OR amount<=-5 AND amount>=-50)" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Builder_composes_and_decomposes(RuleParts parts, string expected)
    {
        var text = RuleExpressionBuilder.Compose(parts, Accounts);
        Assert.Equal(expected, text);
        Assert.True(RuleExpression.IsValid(text));
        Assert.Equal(parts, RuleExpressionBuilder.TryDecompose(text, Accounts));
    }

    [Fact]
    public void Composed_conditions_keep_their_meaning()
    {
        var expense = RuleExpressionBuilder.Compose(new RuleParts { Payee = "example", Min = 5, Max = 50 }, Accounts);
        Assert.True(M(expense));
        Assert.True(M(expense, Market with { Amount = 1250 }));
        Assert.False(M(expense, Market with { Amount = -100 }));
        Assert.False(M(expense, Market with { Amount = -9000 }));
        var onAccount = RuleExpressionBuilder.Compose(new RuleParts { Payee = "example", AccountId = 7 }, Accounts);
        Assert.True(M(onAccount));
        Assert.False(M(onAccount, Market with { LocalAccount = "DE11" }));
    }

    [Theory]
    [InlineData("market OR bakery")]
    [InlineData("name:market")]                 // bare word: not the composed (quoted) shape
    [InlineData("purpose:\"x\"")]
    [InlineData("NOT name:\"a\"")]
    [InlineData("local_account=\"DE55\"")]      // not one of the project's accounts
    [InlineData("(market")]
    public void Other_shapes_are_text_only(string expression) =>
        Assert.Null(RuleExpressionBuilder.TryDecompose(expression, Accounts));

    [Fact]
    public void Empty_expression_decomposes_to_empty_inputs() =>
        Assert.Equal(new RuleParts(), RuleExpressionBuilder.TryDecompose("", Accounts));

    [Theory]
    [InlineData("mandate:M-0009", true)]
    [InlineData("mandate:M-0001", false)]          // the stored field wins over the purpose's MREF
    [InlineData("creditor_id=\"DE99ZZZ0000009\"", true)]
    [InlineData("creditor_id:DE00ZZZ0000001", false)]
    [InlineData("end_to_end:E2E-9", true)]
    [InlineData("reference:E2E-9", true)]
    [InlineData("reference:REF-77", true)]
    [InlineData("reference:\"Card payment\"", false)] // with stored references the purpose is not searched
    public void Sepa_fields_use_the_stored_values(string expression, bool expected)
    {
        var subject = Market with { EndToEndReference = "E2E-9", MandateReference = "M-0009", CreditorId = "DE99ZZZ0000009" };
        Assert.Equal(expected, M(expression, subject));
    }

    [Theory]
    [InlineData("end_to_end:123", true)]           // no stored value: the purpose is searched
    [InlineData("reference:EREF", true)]
    public void Sepa_fields_fall_back_to_the_purpose(string expression, bool expected)
    {
        Assert.Equal(expected, M(expression, Market with { BankReference = null }));
    }
}
