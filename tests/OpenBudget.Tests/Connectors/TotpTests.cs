#pragma warning disable CA2000 // test objects live for the test
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.PayPal;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>TOTP codes (RFC 6238), secret parsing, the stored extra credential and the web login's code autofill.</summary>
public sealed class TotpTests
{
    // RFC 6238 appendix B seeds; base32 of the 20-byte SHA-1 seed "12345678901234567890"
    private const string Sha1Seed = "12345678901234567890";
    private const string Sha1Base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Codes_match_the_RFC_6238_test_vectors(long unixSeconds, string sha1, string sha256, string sha512)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(sha1, TotpSecret.FromKey(Encoding.ASCII.GetBytes(Sha1Seed), 8).CodeAt(time));
        Assert.Equal(sha256, TotpSecret.FromKey(Encoding.ASCII.GetBytes("12345678901234567890123456789012"), 8, algorithm: HashAlgorithmName.SHA256).CodeAt(time));
        Assert.Equal(sha512, TotpSecret.FromKey(Encoding.ASCII.GetBytes(
            "1234567890123456789012345678901234567890123456789012345678901234"), 8, algorithm: HashAlgorithmName.SHA512).CodeAt(time));
    }

    [Theory]
    [InlineData(Sha1Base32)]
    [InlineData("gezd gnbv gy3t qojq gezd gnbv gy3t qojq")]
    [InlineData("  GEZD-GNBV-GY3T-QOJQ-GEZD-GNBV-GY3T-QOJQ  ")]
    public void A_base32_key_is_read_with_spaces_dashes_and_lowercase(string text)
    {
        var secret = TotpSecret.Parse(text);
        Assert.Equal(6, secret.Digits);
        Assert.Equal(30, secret.Period);
        // the six-digit code is the last six digits of the RFC's eight-digit vector
        Assert.Equal("287082", secret.CodeAt(DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public void An_otpauth_uri_supplies_secret_digits_period_and_algorithm()
    {
        var secret = TotpSecret.Parse($"otpauth://totp/Example:user%40example.org?secret=gezdgnbvgy3tqojqgezdgnbvgy3tqojq&issuer=Example&digits=8&period=60&algorithm=SHA1");
        Assert.Equal(8, secret.Digits);
        Assert.Equal(60, secret.Period);
        Assert.Equal(HashAlgorithmName.SHA1, secret.Algorithm);
        // with a 60 s period, t=118 is step 1, the same counter as t=59 with 30 s
        Assert.Equal("94287082", secret.CodeAt(DateTimeOffset.FromUnixTimeSeconds(118)));
        Assert.Equal(HashAlgorithmName.SHA256, TotpSecret.Parse($"otpauth://totp/x?secret={Sha1Base32}&algorithm=sha256").Algorithm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base32 !")]
    [InlineData("GEZDGNBV")] // too short
    [InlineData("otpauth://hotp/x?secret=GEZDGNBVGY3TQOJQ")]
    [InlineData("otpauth://totp/x?issuer=Example")]
    [InlineData("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQ&algorithm=MD5")]
    [InlineData("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQ&digits=4")]
    public void Unusable_secrets_are_refused_without_echoing_them(string text)
    {
        var ex = Assert.Throws<FormatException>(() => TotpSecret.Parse(text));
        if (text.Length > 0) Assert.DoesNotContain(text, ex.Message, StringComparison.Ordinal);
        Assert.False(TotpSecret.TryParse(text, out _));
    }

    [Fact]
    public void The_secret_is_never_printed()
    {
        var text = TotpSecret.Parse(Sha1Base32).ToString();
        Assert.DoesNotContain("GEZD", text, StringComparison.OrdinalIgnoreCase);
        var prompt = new WebLoginCodePrompt("/mfa", "#code", "#verify", "Enter the code.") { Totp = TotpSecret.Parse(Sha1Base32) };
        Assert.DoesNotContain("GEZD", prompt.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(999_999_995L, -1)] // early in the window: the previous one
    [InlineData(1_000_000_015L, 1)] // late in the window: the next one
    public void The_second_attempt_uses_the_adjacent_window_and_a_third_asks_the_user(long unixSeconds, int expectedOffset)
    {
        var secret = TotpSecret.Parse(Sha1Base32);
        var now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(secret.CodeAt(now), TotpDecision.Next(0, secret, now));
        Assert.Equal(secret.CodeAt(now, expectedOffset), TotpDecision.Next(1, secret, now));
        Assert.Null(TotpDecision.Next(2, secret, now));
    }

    // ---- stored credential ----

    [Fact]
    public async Task The_PayPal_login_stores_an_optional_TOTP_secret_encrypted_with_the_password()
    {
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [PayPalConnector.CreateRegistration(new PayPalOptions())] });
        var service = world.For(BankWorld.Alice);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateLoginAsync(PayPalConnector.TypeName, "PayPal", "alice@example.org", "example-password",
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["totp"] = "not base32 !" }));

        var plain = await service.CreateLoginAsync(PayPalConnector.TypeName, "PayPal", "alice@example.org", "example-password");
        Assert.Empty(plain.StoredExtraCredentials!);

        var view = await service.CreateLoginAsync(PayPalConnector.TypeName, "PayPal", "alice@example.org", "example-password",
            extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["totp"] = " gezd gnbv gy3t qojq " });
        Assert.Equal(["totp"], view.StoredExtraCredentials);
        Assert.Equal("gezd gnbv gy3t qojq", (await StoredAsync(world, view.Id)).ExtraValue("totp"));

        // the edit dialog: a password change keeps the secret, an empty value removes it
        await service.UpdateLoginAsync(view.Id, "PayPal", RefreshInterval.EveryTwoHours, secret: "other-password");
        Assert.NotNull((await StoredAsync(world, view.Id)).ExtraValue("totp"));
        await service.UpdateLoginAsync(view.Id, "PayPal", RefreshInterval.EveryTwoHours, extra: new Dictionary<string, string>(StringComparer.Ordinal) { ["totp"] = "" });
        var cleared = await StoredAsync(world, view.Id);
        Assert.Null(cleared.ExtraValue("totp"));
        Assert.Equal("other-password", cleared.Secret);
    }

    [Fact]
    public void The_PayPal_code_form_gets_the_secret_only_when_one_is_stored()
    {
        var connector = new PayPalConnector(null!, new PayPalOptions());
        Assert.Null(connector.WebLogin(new ConnectorCredentials("u@example.org", "pw")).CodePrompt!.Totp);
        var spec = connector.WebLogin(new ConnectorCredentials("u@example.org", "pw", new Dictionary<string, string>(StringComparer.Ordinal) { ["totp"] = Sha1Base32 }));
        Assert.NotNull(spec.CodePrompt!.Totp);
        Assert.NotEmpty(spec.CodePrompt.ChooseAuthenticator);
    }

    private static async Task<ConnectorCredentials> StoredAsync(BankWorld world, int loginId)
    {
        await using var db = await world.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.AsNoTracking().FirstAsync(l => l.Id == loginId);
        return BankSyncRunner.DecodeCredentials(world.Secrets, login.EncryptedCredentials)!;
    }

    // ---- web login autofill ----

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(999_999_995);

    private static WebLoginSpec Spec(bool withSecret, IReadOnlyList<string>? choose = null) =>
        new(new Uri("https://bank.example/login"), ["bank.example"], new Uri("https://bank.example/home"))
        {
            CodePrompt = new WebLoginCodePrompt("/mfa", "#code", "#verify", "Enter the code.")
            {
                Totp = withSecret ? TotpSecret.Parse(Sha1Base32) : null,
                ChooseAuthenticator = choose ?? [],
                TotpSettle = TimeSpan.FromMilliseconds(200),
            },
        };

    private static WebLoginService Service(FakeBrowserHost host) =>
        new(host, new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero }, new FixedClock(Now), NullLoggerFactory.Instance);

    /// <summary>A code page that accepts <paramref name="accepted"/> (any code when null) and records every code typed.</summary>
    private static FakeBrowserHost CodePage(List<string> typed, Func<string, bool> accepted, bool choiceFirst = false) => new()
    {
        User = page =>
        {
            page.Url = new Uri("https://bank.example/mfa");
            page.Visible.UnionWith(choiceFirst ? ["#use-app", "#verify"] : ["#code", "#verify"]);
            page.OnFill = (p, selector) => { if (selector == "#code") typed.Add(p.Filled[selector]); };
            page.OnClick = (p, selector) =>
            {
                if (selector == "#use-app")
                {
                    lock (p.Visible)
                    {
                        p.Visible.Remove("#use-app");
                        p.Visible.Add("#code");
                    }
                }
                else if (selector == "#verify" && p.Filled.TryGetValue("#code", out var code) && accepted(code))
                {
                    p.Url = new Uri("https://bank.example/home");
                }
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        },
    };

    [Fact]
    public async Task A_stored_secret_fills_the_code_form_without_asking()
    {
        var typed = new List<string>();
        var host = CodePage(typed, _ => true);
        var asked = 0;
        await using var session = await Service(host).StartAsync("user", Spec(withSecret: true), [], CancellationToken.None,
            (c, _) => { asked++; return Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "000000")); });
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal([TotpSecret.Parse(Sha1Base32).CodeAt(Now)], typed);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task The_authenticator_option_is_chosen_before_the_code_is_filled()
    {
        var typed = new List<string>();
        var host = CodePage(typed, _ => true, choiceFirst: true);
        await using var session = await Service(host).StartAsync("user", Spec(withSecret: true, choose: ["#sms", "#use-app"]), [], CancellationToken.None,
            (c, _) => Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "000000")));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal("#use-app", host.Sessions.Single().Clicked[0]);
        Assert.Equal([TotpSecret.Parse(Sha1Base32).CodeAt(Now)], typed);
    }

    [Fact]
    public async Task A_skewed_clock_is_covered_by_the_adjacent_window()
    {
        var secret = TotpSecret.Parse(Sha1Base32);
        var previous = secret.CodeAt(Now, -1);
        var typed = new List<string>();
        var host = CodePage(typed, code => code == previous);
        await using var session = await Service(host).StartAsync("user", Spec(withSecret: true), [], CancellationToken.None,
            (c, _) => Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "000000")));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal([secret.CodeAt(Now), previous], typed);
    }

    [Fact]
    public async Task A_rejected_secret_falls_back_to_the_shared_code_prompt_with_a_notice()
    {
        var typed = new List<string>();
        var host = CodePage(typed, code => code == "424242");
        var messages = new List<string>();
        await using var session = await Service(host).StartAsync("user", Spec(withSecret: true), [], CancellationToken.None,
            (c, _) =>
            {
                messages.Add(c.Message);
                return Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "424242"));
            });
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal(3, typed.Count);
        Assert.Equal("424242", typed[^1]);
        Assert.Equal(["The stored TOTP secret was not accepted. Enter the code."], messages);
    }

    [Fact]
    public async Task Without_a_secret_the_user_is_asked_as_before()
    {
        var typed = new List<string>();
        var host = CodePage(typed, _ => true);
        var messages = new List<string>();
        await using var session = await Service(host).StartAsync("user", Spec(withSecret: false), [], CancellationToken.None,
            (c, _) =>
            {
                messages.Add(c.Message);
                return Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "135790"));
            });
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
        Assert.Equal(["135790"], typed);
        Assert.Equal(["Enter the code."], messages);
    }
}
