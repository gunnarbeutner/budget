#pragma warning disable CA2000 // test objects live for the test
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The Amazon sign-in against synthetic pages of its flow: e-mail page, password page, one-time code page, then the
/// wallet. Stored credentials are filled in with the page hidden, a stored TOTP secret answers the code page; wrong
/// credentials, a captcha or a code page without a secret stop for the user.
/// </summary>
public sealed class AmazonSignInTests
{
    private const string TotpKey = "JBSWY3DPEHPK3PXP";
    private const string Email = "someone@example.com";
    private const string Password = "Example-Password1!";
    private static readonly Uri Base = new("https://shop.test/");
    private const string Step1Submit = AmazonSignIn.ContinueSelector + ", " + AmazonSignIn.SignInSelector;

    private static ConnectorCredentials Credentials(bool totp) => new(Email, Password,
        totp ? new Dictionary<string, string>(StringComparer.Ordinal) { [AmazonSignIn.TotpKey] = TotpKey } : null);

    private static WebLoginSpec Spec(ConnectorCredentials? credentials) =>
        new AmazonConnector(null!, new AmazonOptions { BaseUri = Base }).WebLogin(credentials);

    private static WebLoginService Service(FakeBrowserHost host) => new(host, new WebLoginOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        SettleDelay = TimeSpan.Zero,
        FormTimeout = TimeSpan.FromMilliseconds(500),
        UnattendedTimeout = TimeSpan.FromSeconds(2),
        StuckAfter = TimeSpan.FromSeconds(1),
    }, TimeProvider.System, NullLoggerFactory.Instance);

    private static void Show(FakeBrowserSession p, string path, params string[] selectors)
    {
        lock (p.Visible)
        {
            p.Url = new Uri(Base, path);
            p.Visible.Clear();
            p.Visible.UnionWith(selectors);
        }
    }

    /// <summary>
    /// amazon.de as far as the sign-in goes: the wallet redirects to the e-mail page until signed in; after the password
    /// comes <paramref name="afterPassword"/> (the code page, say). The waiting script's verdict is played by the page:
    /// a captcha needs the user.
    /// </summary>
    private static FakeBrowserHost Amazon(Action<FakeBrowserSession> afterPassword)
    {
        var signedIn = false;
        var host = new FakeBrowserHost { FieldWait = TimeSpan.FromMilliseconds(200) };
        host.Redirect = u => !signedIn && u.AbsolutePath.StartsWith("/cpe/", StringComparison.Ordinal) ? new Uri(Base, "ap/signin") : null;
        host.User = page =>
        {
            if (page.Url.AbsolutePath != "/ap/signin") return Task.CompletedTask;
            Show(page, "ap/signin", AmazonSignIn.EmailSelector, Step1Submit);
            page.OnCheck = p => p.Url.AbsolutePath.Contains("captcha", StringComparison.Ordinal)
                ? new WebLoginCheck(false, "Amazon shows a captcha.")
                : new WebLoginCheck(true, "");
            page.OnClick = (p, selector) =>
            {
                if (selector == Step1Submit && p.Filled.ContainsKey(AmazonSignIn.EmailSelector) && !p.Filled.ContainsKey(AmazonSignIn.PasswordSelector))
                    Show(p, "ap/signin", AmazonSignIn.PasswordSelector, AmazonSignIn.RememberMeSelector, AmazonSignIn.SignInSelector);
                else if (selector == AmazonSignIn.SignInSelector)
                    afterPassword(p);
                else if (selector == AmazonSignIn.CodeSubmitSelector)
                {
                    signedIn = true;
                    Show(p, "cpe/yourpayments/transactions");
                }
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        };
        return host;
    }

    private static void CodePage(FakeBrowserSession p) =>
        Show(p, "ap/mfa", AmazonSignIn.CodeSelector, AmazonSignIn.RememberDeviceSelector, AmazonSignIn.CodeSubmitSelector);

    private static async Task<(WebLoginResult Result, FakeBrowserSession Page, List<WebLoginMode> Modes, int Asked)> RunAsync(
        FakeBrowserHost host, WebLoginSpec spec, WebLoginUnattended? unattended = null)
    {
        var modes = new List<WebLoginMode>();
        var asked = 0;
        await using var session = unattended is { } mode
            ? await Service(host).StartAsync("user", spec, [], CancellationToken.None, unattended: mode)
            : await Service(host).StartAsync("user", spec, [], CancellationToken.None,
                (c, _) => { asked++; return Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(c, "123456")); });
        session.ModeChanged += m => { lock (modes) modes.Add(m); };
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        lock (modes) return (result, host.Sessions.Single(), modes.ToList(), asked);
    }

    [Fact]
    public async Task Stored_credentials_and_a_totp_secret_sign_in_with_the_page_hidden()
    {
        var (result, page, modes, asked) = await RunAsync(Amazon(CodePage), Spec(Credentials(totp: true)));
        Assert.True(result.Success, result.Error);
        Assert.Equal(Email, page.Filled[AmazonSignIn.EmailSelector]);
        Assert.Equal(Password, page.Filled[AmazonSignIn.PasswordSelector]);
        Assert.Equal(TotpSecret.Parse(TotpKey).CodeAt(DateTimeOffset.UtcNow).Length, page.Filled[AmazonSignIn.CodeSelector].Length);
        Assert.Contains(AmazonSignIn.RememberMeSelector, page.Ticked);
        Assert.Contains(AmazonSignIn.RememberDeviceSelector, page.Ticked);
        Assert.Equal(0, asked);
        Assert.DoesNotContain(WebLoginMode.Interactive, modes);
    }

    [Fact]
    public async Task Without_a_secret_the_code_is_asked_from_the_user()
    {
        var (result, page, _, asked) = await RunAsync(Amazon(CodePage), Spec(Credentials(totp: false)));
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, asked);
        Assert.Equal("123456", page.Filled[AmazonSignIn.CodeSelector]);
    }

    [Fact]
    public async Task A_background_run_signs_in_with_a_secret_and_stops_at_a_code_page_without_one()
    {
        var (withSecret, _, _, _) = await RunAsync(Amazon(CodePage), Spec(Credentials(totp: true)), WebLoginUnattended.SignIn);
        Assert.True(withSecret.Success, withSecret.Error);

        var (without, page, _, _) = await RunAsync(Amazon(CodePage), Spec(Credentials(totp: false)), WebLoginUnattended.SignIn);
        Assert.True(without.NeedsUser);
        Assert.False(page.Filled.ContainsKey(AmazonSignIn.CodeSelector));
    }

    [Fact]
    public async Task A_rejected_password_ends_as_rejected_credentials_and_is_submitted_once()
    {
        var host = Amazon(p =>
        {
            Show(p, "ap/signin", AmazonSignIn.PasswordSelector, AmazonSignIn.SignInSelector, AmazonSignIn.ErrorSelector);
            lock (p.Visible) p.Texts[AmazonSignIn.ErrorSelector] = "Your password is incorrect";
        });
        var (result, page, _, _) = await RunAsync(host, Spec(Credentials(totp: true)), WebLoginUnattended.SignIn);
        Assert.True(result.CredentialsRejected);
        Assert.Single(page.Clicked, AmazonSignIn.SignInSelector);
    }

    [Fact]
    public async Task A_captcha_is_shown_to_the_user_and_stops_a_background_run()
    {
        var (background, _, _, _) = await RunAsync(Amazon(p => Show(p, "ap/captcha")), Spec(Credentials(totp: true)), WebLoginUnattended.SignIn);
        Assert.True(background.NeedsUser);
    }

    [Fact]
    public void The_spec_fills_only_stored_credentials_and_uses_the_secret_only_when_stored()
    {
        Assert.Null(Spec(null).Autofill);
        Assert.Null(Spec(new ConnectorCredentials("", "")).Autofill);
        Assert.Null(Spec(Credentials(totp: false)).CodePrompt!.Totp);
        Assert.NotNull(Spec(Credentials(totp: true)).CodePrompt!.Totp);
        Assert.DoesNotContain(Password, Spec(Credentials(totp: true)).Autofill!.ToString(), StringComparison.Ordinal);

        var registration = AmazonConnector.CreateRegistration(new AmazonOptions());
        Assert.Equal(CredentialRequirement.UsernameAndSecret, registration.Credentials);
        Assert.Equal(SilentCapability.Full, registration.SilentCapabilityFor(Credentials(totp: true)));
        Assert.Equal(SilentCapability.SessionOnly, registration.SilentCapabilityFor(Credentials(totp: false)));
        Assert.True(registration.SilentWebSignIn);
    }
}
