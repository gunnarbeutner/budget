using Xunit;
using OpenBudget.Services;

namespace OpenBudget.Tests;

public class RuleExpressionNamesTests
{
    private static readonly RestrictionTarget[] Targets =
    [
        new(false, 22, "PayPal", 0), new(false, 3, "Girokonto", 0), new(false, 4, "Girokonto", 0), new(true, 5, "Family", 0),
    ];

    [Fact]
    public void Ids_show_as_unique_names_and_go_back_to_ids()
    {
        const string stored = "name:\"XS News B.V.\" AND account_id=22 AND NOT account_group=5";
        var shown = RuleExpressionNames.ToDisplay(stored, Targets);
        Assert.Equal("name:\"XS News B.V.\" AND account:\"PayPal\" AND NOT account_group:\"Family\"", shown);
        Assert.Equal(stored, RuleExpressionNames.FromDisplay(shown, Targets, out var error));
        Assert.Null(error);
        Assert.Equal("account_id!=22", RuleExpressionNames.FromDisplay("account!=\"paypal\"", Targets, out _));
    }

    [Fact]
    public void Shared_names_quoted_text_and_unknown_names_stay_as_they_are()
    {
        Assert.Equal("account_id=3", RuleExpressionNames.ToDisplay("account_id=3", Targets));
        Assert.Equal("\"account_id=22\" OR /account_id=22/", RuleExpressionNames.ToDisplay("\"account_id=22\" OR /account_id=22/", Targets));
        Assert.Equal("account:\"Girokonto\"", RuleExpressionNames.FromDisplay("account:\"Girokonto\"", Targets, out var ambiguous));
        Assert.NotNull(ambiguous);
        RuleExpressionNames.FromDisplay("account:\"Nope\"", Targets, out var unknown);
        Assert.NotNull(unknown);
    }
}
