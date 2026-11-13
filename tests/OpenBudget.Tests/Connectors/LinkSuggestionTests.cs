using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Which imported account a bank account is suggested to link to: same IBAN, else the same account number.</summary>
public sealed class LinkSuggestionTests
{
    [Theory]
    [InlineData("DE00 1234 5678 0001 2345 67", "1234567")]
    [InlineData("0001234567", "1234567")]
    [InlineData("1234567-01", "1234567")]
    [InlineData(" 12 34 567 ", "1234567")]
    [InlineData("FR7630006000011234567890189", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void The_account_number_comes_from_an_IBAN_or_a_plain_number(string? value, string expected) =>
        Assert.Equal(expected, BankLoginService.AccountNumber(value));

    [Fact]
    public async Task Without_an_IBAN_match_the_account_with_the_same_account_number_is_suggested()
    {
        await using var w = await BankWorld.CreateAsync();
        // The imported giro carries the giro's account number only (no IBAN).
        var giroNumber = FakeBankConnector.GiroIban.Replace(" ", "", StringComparison.Ordinal)[^10..];
        await w.ImportMoneyMoneyAsync(w.BuildSnapshot([("MM Giro", giroNumber), ("MM Other", "DE00000000000000009999")], []));
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);

        var candidates = await w.For(BankWorld.Alice).GetLinkCandidatesAsync(giro.Id);
        Assert.Equal("MM Giro", Assert.Single(candidates, c => c.SuggestedFor).Name);
    }
}
