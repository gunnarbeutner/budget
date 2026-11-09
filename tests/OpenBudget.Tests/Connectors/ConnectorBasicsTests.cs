using System.Text;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Connectors;

public sealed class ConnectorBasicsTests
{
    // ---- secrets ----

    [Fact]
    public void Encryption_round_trips_and_is_randomized()
    {
        var p = new SecretsProtector(SecretsProtector.GenerateKey());
        Assert.True(p.IsConfigured);
        var a = p.Encrypt("alice-pin-1234", "purpose");
        var b = p.Encrypt("alice-pin-1234", "purpose");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("alice-pin", a, StringComparison.Ordinal);
        Assert.True(p.TryDecrypt(a, "purpose", out var plain));
        Assert.Equal("alice-pin-1234", plain);
    }

    [Fact]
    public void Wrong_key_tampering_and_wrong_purpose_fail_without_throwing()
    {
        var p = new SecretsProtector(SecretsProtector.GenerateKey());
        var other = new SecretsProtector(SecretsProtector.GenerateKey());
        var payload = p.Encrypt("secret", "purpose");

        Assert.False(other.TryDecrypt(payload, "purpose", out _));
        Assert.False(p.TryDecrypt(payload, "other-purpose", out _));
        var tampered = payload[..^3] + (payload.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");
        Assert.False(p.TryDecrypt(tampered, "purpose", out _));
        Assert.False(p.TryDecrypt("garbage", "purpose", out _));
        Assert.False(p.TryDecrypt("v1:!!!not-base64", "purpose", out _));
        Assert.False(p.TryDecrypt(null, "purpose", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void Missing_or_invalid_key_disables_the_feature(string? key)
    {
        var p = new SecretsProtector(key);
        Assert.False(p.IsConfigured);
        Assert.False(string.IsNullOrEmpty(p.ConfigurationProblem));
        Assert.Throws<InvalidOperationException>(() => p.Encrypt("x", "purpose"));
        Assert.False(p.TryDecrypt("v1:AAAA", "purpose", out _));
        if (!string.IsNullOrEmpty(key)) Assert.DoesNotContain(key, p.ConfigurationProblem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_key_is_32_bytes_of_base64()
    {
        Assert.Equal(32, Convert.FromBase64String(SecretsProtector.GenerateKey()).Length);
    }

    [Fact]
    public void Credentials_do_not_leak_through_ToString()
    {
        var text = new ConnectorCredentials("alice", "hunter2").ToString();
        Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    // ---- refresh policy ----

    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(RefreshInterval.EveryTwoHours, 119, false)]
    [InlineData(RefreshInterval.EveryTwoHours, 121, true)]
    [InlineData(RefreshInterval.FourTimesDaily, 359, false)]
    [InlineData(RefreshInterval.FourTimesDaily, 361, true)]
    [InlineData(RefreshInterval.Daily, 1439, false)]
    [InlineData(RefreshInterval.Daily, 1441, true)]
    [InlineData(RefreshInterval.Weekly, 10079, false)]
    [InlineData(RefreshInterval.Weekly, 10081, true)]
    [InlineData(RefreshInterval.ManualOnly, 100000, false)]
    [InlineData(RefreshInterval.Never, 100000, false)]
    public void Page_load_trigger_respects_each_interval(RefreshInterval interval, int minutesAgo, bool due)
    {
        Assert.Equal(due, RefreshPolicy.IsDue(interval, Now.AddMinutes(-minutesAgo), Now));
    }

    [Theory]
    [InlineData(RefreshInterval.EveryTwoHours)]
    [InlineData(RefreshInterval.Weekly)]
    public void A_never_synced_login_is_due_unless_manual(RefreshInterval interval) =>
        Assert.True(RefreshPolicy.IsDue(interval, null, Now));

    [Fact]
    public void Manual_and_never_logins_are_never_due_even_when_never_synced()
    {
        Assert.False(RefreshPolicy.IsDue(RefreshInterval.ManualOnly, null, Now));
        Assert.False(RefreshPolicy.IsDue(RefreshInterval.Never, null, Now));
    }

    [Fact]
    public void Sync_all_includes_everything_but_never()
    {
        foreach (var i in Enum.GetValues<RefreshInterval>())
            Assert.Equal(i != RefreshInterval.Never, RefreshPolicy.IncludedInSyncAll(i));
    }

    private static BankLoginAccount StatementAccount(StatementsInterval interval, DateTime? attempt = null, DateTime? got = null, bool enabled = true, bool closed = false, long? linked = 1) => new()
    {
        StatementsEnabled = enabled,
        StatementsInterval = interval,
        LastStatementsAttemptAt = attempt,
        LastStatementAt = got,
        Closed = closed,
        LinkedAccountId = linked,
    };

    [Fact]
    public void Statements_are_never_due_when_off_closed_unlinked_or_manual()
    {
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily, enabled: false), Now));
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily, closed: true), Now));
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily, linked: null), Now));
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.ManualOnly), Now));
    }

    [Fact]
    public void Monthly_statements_retry_daily_until_one_arrived_this_month()
    {
        // Nothing yet this month, never tried: due.
        Assert.True(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Monthly), Now));
        // Tried a few hours ago without success: wait, then retry the next day.
        var tried = StatementAccount(StatementsInterval.Monthly, attempt: Now.AddHours(-5), got: new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(RefreshPolicy.StatementsDue(tried, Now));
        Assert.True(RefreshPolicy.StatementsDue(tried, Now.AddHours(20)));
        // A statement arrived this month: done until the next month starts.
        var got = StatementAccount(StatementsInterval.Monthly, attempt: Now.AddDays(-1), got: Now.AddDays(-1));
        Assert.False(RefreshPolicy.StatementsDue(got, Now.AddDays(10)));
        Assert.False(RefreshPolicy.StatementsDue(got, new DateTime(2026, 11, 30, 23, 59, 0, DateTimeKind.Utc)));
        Assert.True(RefreshPolicy.StatementsDue(got, new DateTime(2026, 12, 1, 0, 1, 0, DateTimeKind.Utc)));
        // An attempt late on the last day of a month does not delay the try at the start of the next one.
        var lateTry = StatementAccount(StatementsInterval.Monthly, attempt: new DateTime(2026, 11, 30, 23, 0, 0, DateTimeKind.Utc), got: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(RefreshPolicy.StatementsDue(lateTry, new DateTime(2026, 12, 1, 1, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Weekly_and_daily_statements_follow_the_last_attempt()
    {
        Assert.True(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily), Now));
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily, attempt: Now.AddHours(-23)), Now));
        Assert.True(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Daily, attempt: Now.AddHours(-25)), Now));
        Assert.False(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Weekly, attempt: Now.AddDays(-6)), Now));
        Assert.True(RefreshPolicy.StatementsDue(StatementAccount(StatementsInterval.Weekly, attempt: Now.AddDays(-8)), Now));
    }

    // ---- http client and fake bank bits ----

    [Fact]
    public void Http_client_cookie_jar_survives_export_and_import()
    {
        using var first = new BankHttpClient();
        first.ImportCookies([new CookieState("sid", "abc", "bank.example", "/", DateTime.UtcNow.AddDays(1), true, true)]);
        var exported = first.ExportCookies();
        var cookie = Assert.Single(exported);
        Assert.Equal("sid", cookie.Name);
        Assert.Equal("abc", cookie.Value);

        using var second = new BankHttpClient();
        second.ImportCookies(exported);
        Assert.Equal("abc", Assert.Single(second.ExportCookies()).Value);
    }

    [Fact]
    public void Expired_cookies_are_not_restored()
    {
        using var client = new BankHttpClient();
        client.ImportCookies([new CookieState("old", "x", "bank.example", "/", DateTime.UtcNow.AddDays(-1), false, false)]);
        Assert.Empty(client.ExportCookies());
    }

    [Fact]
    public void Fake_pdf_is_structurally_valid()
    {
        var text = Encoding.ASCII.GetString(FakePdf.Create("Example (test)"));
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startxref = int.Parse(text.Split("startxref\n")[1].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startxref..], StringComparison.Ordinal);
    }

    [Fact]
    public void Fake_bank_flags_come_from_the_username()
    {
        var s = FakeBankSettings.Parse("demo+code+approve3+pending+noid");
        Assert.True(s.Code);
        Assert.True(s.Approval);
        Assert.Equal(3, s.ApprovalPolls);
        Assert.True(s.Pending);
        Assert.True(s.NoIds);
        Assert.False(s.Image);
        Assert.False(FakeBankSettings.Parse("code").Code); // the first token is only a label
    }

    [Fact]
    public async Task Fake_bank_data_is_obviously_fake()
    {
        using var http = new BankHttpClient();
        var bank = new FakeBankConnector(new ConnectorContext(http, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        var accounts = await bank.ListAccountsAsync(CancellationToken.None);
        Assert.All(accounts.Where(a => a.Iban != null), a => Assert.StartsWith("DE00", a.Iban, StringComparison.Ordinal));
    }
}
