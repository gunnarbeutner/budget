using System.Text;
using OpenBudget.Importers.MoneyMoney;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Reading MoneyMoney's category budgets from <c>categories.userdata</c> and spreading them over the months.</summary>
public sealed class MoneyMoneyBudgetImportTests
{
    private static MoneyMoneyCategoryBudget? Parse(string text) => MoneyMoneyBudgetImport.Parse(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("6:budget,5:180.0,14:budgetInterval,1:0,", 180, MoneyMoneyBudgetInterval.Monthly)]
    [InlineData("14:showInDiagrams,1:1,6:budget,4:90.5,14:budgetInterval,1:1,", 90.5, MoneyMoneyBudgetInterval.Quarterly)]
    [InlineData("6:budget,6:1200.0,14:budgetInterval,1:2,4:uuid,3:abc,", 1200, MoneyMoneyBudgetInterval.Yearly)]
    [InlineData("6:budget,5:500.0,14:budgetInterval,1:3,", 500, MoneyMoneyBudgetInterval.Total)]
    [InlineData("6:budget,6:-75.25,", 75.25, MoneyMoneyBudgetInterval.Monthly)]
    public void Budgets_are_read_with_their_interval(string userdata, double amount, MoneyMoneyBudgetInterval interval) =>
        Assert.Equal(new MoneyMoneyCategoryBudget((decimal)amount, interval), Parse(userdata));

    [Theory]
    [InlineData("6:budget,1:0,14:budgetInterval,1:0,")]
    [InlineData("6:budget,3:0.0,")]
    [InlineData("14:showInReports,1:0,")]
    [InlineData("")]
    [InlineData("6:budget,5:180")]
    [InlineData("x:budget,5:180.0,")]
    [InlineData("6:budget,5:180.0,14:budgetInterval,1:9,")]
    public void Zero_missing_or_malformed_budgets_are_none(string userdata) => Assert.Null(Parse(userdata));

    [Fact]
    public void Bytes_that_are_not_UTF8_do_not_break_parsing()
    {
        byte[] blob = [.. Encoding.ASCII.GetBytes("4:name,2:"), 0xff, 0xfe, .. Encoding.ASCII.GetBytes(",6:budget,4:12.0,")];
        Assert.Equal(new MoneyMoneyCategoryBudget(12m, MoneyMoneyBudgetInterval.Monthly), MoneyMoneyBudgetImport.Parse(blob));
    }

    [Fact]
    public void Quarterly_and_yearly_budgets_spread_evenly_with_the_remainder_in_December()
    {
        var quarterly = MoneyMoneyBudgetImport.Months(new MoneyMoneyCategoryBudget(100m, MoneyMoneyBudgetInterval.Quarterly))!;
        Assert.Equal(33.33m, quarterly[0]);
        Assert.Equal(400m, quarterly.Sum());
        Assert.Equal(400m - (33.33m * 11), quarterly[11]);
        var yearly = MoneyMoneyBudgetImport.Months(new MoneyMoneyCategoryBudget(1000m, MoneyMoneyBudgetInterval.Yearly))!;
        Assert.Equal(83.33m, yearly[0]);
        Assert.Equal(1000m, yearly.Sum());
        Assert.All(MoneyMoneyBudgetImport.Months(new MoneyMoneyCategoryBudget(50m, MoneyMoneyBudgetInterval.Monthly))!, m => Assert.Equal(50m, m));
        Assert.Null(MoneyMoneyBudgetImport.Months(new MoneyMoneyCategoryBudget(50m, MoneyMoneyBudgetInterval.Total)));
    }
}
