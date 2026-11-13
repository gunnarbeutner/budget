using System.Globalization;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class AccountDisplayTests
{
    [Theory]
    [InlineData("DE00123456780123456789", "DE00 1234 5678 0123 4567 89")]
    [InlineData("de00 1234 5678 0123 4567 89", "DE00 1234 5678 0123 4567 89")]
    [InlineData("DE00", "DE00")]
    public void Iban_is_grouped_in_fours(string input, string expected) =>
        Assert.Equal(expected, AccountDisplay.FormatIban(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_iban_gives_null(string? input) => Assert.Null(AccountDisplay.FormatIban(input));

    [Fact]
    public void Connector_success_wins_over_the_account_refresh_timestamp()
    {
        var linkAt = new DateTime(2026, 11, 10, 19, 10, 0, DateTimeKind.Utc);
        var refresh = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.Equal(linkAt, AccountDisplay.SelectLastUpdated(linkAt, refresh));
    }

    [Fact]
    public void Without_a_connector_success_the_refresh_timestamp_is_used()
    {
        var refresh = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), AccountDisplay.SelectLastUpdated(null, refresh));
        Assert.Null(AccountDisplay.SelectLastUpdated(null, null));
        Assert.Null(AccountDisplay.SelectLastUpdated(null, 0));
    }

    // Newer ICU data separates the time and AM/PM with a narrow no-break space.
    private static string Plain(string s) => s.Replace('\u202f', ' ');

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Same_local_day_reads_today_in_the_given_zone()
    {
        var now = new DateTime(2026, 11, 10, 19, 30, 0, DateTimeKind.Utc);       // 20:30 in Berlin
        var updated = new DateTime(2026, 11, 10, 19, 10, 0, DateTimeKind.Utc);   // 20:10 in Berlin

        Assert.Equal("Today, 8:10 PM", Plain(AccountDisplay.FormatLastUpdated(updated, Berlin, now, En, "Today", "Yesterday")));
    }

    [Fact]
    public void The_local_day_decides_not_the_utc_day()
    {
        // 23:30 UTC is 00:30 the next day in Berlin: "now" is already the 11th there.
        var now = new DateTime(2026, 11, 10, 23, 30, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);   // 23:00 on the 10th in Berlin

        Assert.StartsWith("Yesterday, ", AccountDisplay.FormatLastUpdated(updated, Berlin, now, En, "Today", "Yesterday"), StringComparison.Ordinal);
    }

    [Fact]
    public void Older_updates_show_the_date()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("11/3/2026, 1:00 PM", Plain(AccountDisplay.FormatLastUpdated(updated, Berlin, now, En, "Today", "Yesterday")));
        Assert.Equal("03.11.2026, 13:00", AccountDisplay.FormatLastUpdated(updated, Berlin, now, CultureInfo.GetCultureInfo("de-DE"), "Heute", "Gestern"));
    }

    [Fact]
    public async Task Header_details_come_from_the_link_for_its_owner_and_from_the_account_for_a_sharee()
    {
        await using var t = await AccessWorld.BuildAsync();
        var success = new DateTime(2026, 11, 10, 19, 10, 0, DateTimeKind.Utc);
        var acc = t.Db.Accounts.Single(a => a.Id == AccessWorld.AccountWp);
        acc.Iban = "DE00123456780123456789";
        acc.RefreshTimestamp = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var login = new BankLogin { OwnerUserId = AccessWorld.Alice, ConnectorType = "fake", DisplayName = "Example Bank" };
        t.Db.BankLogins.Add(login);
        await t.Db.SaveChangesAsync();
        t.Db.BankLoginAccounts.Add(new BankLoginAccount
        {
            BankLoginId = login.Id,
            RemoteId = "R1",
            Name = "Giro",
            LinkedAccountId = acc.Id,
            LastSuccessAt = success,
            LastError = "Bank unreachable",
        });
        t.Db.Transactions.Add(new Transaction { Id = 900, OwnerUserId = AccessWorld.Alice, AccountId = acc.Id, Amount = -2895, Booked = false, Timestamp = 1, Name = "Pending shop" });
        await t.Db.SaveChangesAsync();

        var owner = (await t.ForUser(AccessWorld.Alice).Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3))!.Accounts.Single(a => a.Id == acc.Id);
        var sharee = (await t.ForUser(AccessWorld.Bob).Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3))!.Accounts.Single(a => a.Id == acc.Id);

        Assert.Equal("DE00123456780123456789", owner.Iban);
        Assert.Equal("Example Bank", owner.Institution);
        Assert.Equal(success, owner.LastUpdatedUtc);
        Assert.Equal("Bank unreachable", owner.Error);
        Assert.Equal(-28.95m, owner.PendingBalance);
        Assert.Equal(login.Id, owner.SyncLoginId);

        Assert.Null(sharee.Institution);
        Assert.Null(sharee.SyncLoginId);
        Assert.Null(sharee.Error);
        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), sharee.LastUpdatedUtc);
        Assert.Equal(-28.95m, sharee.PendingBalance);
    }
}
