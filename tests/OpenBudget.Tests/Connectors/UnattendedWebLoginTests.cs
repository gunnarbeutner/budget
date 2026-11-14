#pragma warning disable CA2000 // test objects live for the test
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// Web logins in background runs, against fake pages of a sign-in flow like PayPal's: only a stored session or the
/// stored credentials plus a stored TOTP secret get through; anything else stops for the owner with nothing more submitted.
/// </summary>
public sealed class UnattendedWebLoginTests
{
    private const string Domain = "pay.example";
    private static readonly Uri Start = new("https://pay.example/signin");
    private static readonly Uri Home = new("https://pay.example/home");
    private const string TotpKey = "JBSWY3DPEHPK3PXP";

    private static WebLoginSpec Spec(bool totp) => new(Start, [Domain], successUrlPrefix: Home)
    {
        Autofill = new WebLoginAutofill("someone@example.com", "Example-Password1!",
            [new WebLoginStep([new WebLoginField("#user", WebLoginCredential.Username), new WebLoginField("#pass", WebLoginCredential.Secret)], "#go")]),
        CodePrompt = new WebLoginCodePrompt("/authflow/", "#code", "#verify", "Enter the code.")
        {
            Totp = totp ? TotpSecret.Parse(TotpKey) : null,
        },
        WaitingScript = "() => ({ ok: !location.pathname.startsWith('/authflow/'), reason: 'Complete the security check.' })",
        CredentialsError = new WebLoginError("#err", ["wrong"], "The bank rejected the credentials."),
        ResumeUrl = Home,
    };

    private static WebLoginService Service(FakeBrowserHost host) => new(host, new WebLoginOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        SettleDelay = TimeSpan.Zero,
        FormTimeout = TimeSpan.FromMilliseconds(300),
        UnattendedTimeout = TimeSpan.FromSeconds(2),
    }, TimeProvider.System, NullLoggerFactory.Instance);

    private static CookieState Cookie(string value) => new("sid", value, Domain, "/", DateTime.UtcNow.AddDays(30), Secure: true, HttpOnly: true, "Lax");

    /// <summary>The sign-in form; submitting it leads to <paramref name="afterSubmit"/>.</summary>
    private static Func<FakeBrowserSession, Task> SignInPage(Action<FakeBrowserSession> afterSubmit) => page =>
    {
        if (page.Url.AbsolutePath != Start.AbsolutePath) return Task.CompletedTask;
        lock (page.Visible) page.Visible.UnionWith(["#user", "#pass", "#go"]);
        page.OnClick = (p, selector) =>
        {
            if (selector == "#go") afterSubmit(p);
            if (selector == "#verify") p.Url = Home;
            return Task.CompletedTask;
        };
        return Task.CompletedTask;
    };

    private static void CodePage(FakeBrowserSession p)
    {
        p.Url = new Uri("https://pay.example/authflow/challenge");
        lock (p.Visible)
        {
            p.Visible.Clear();
            p.Visible.UnionWith(["#code", "#verify"]);
        }
        p.OnCheck = s => s.Url.AbsolutePath.StartsWith("/authflow/", StringComparison.Ordinal)
            ? new WebLoginCheck(false, "Complete the security check.")
            : new WebLoginCheck(true, "");
    }

    private static async Task<(WebLoginResult Result, FakeBrowserSession Page)> RunAsync(
        FakeBrowserHost host, WebLoginSpec spec, WebLoginUnattended mode, IReadOnlyList<CookieState>? seed = null)
    {
        await using var session = await Service(host).StartAsync("user", spec, seed ?? [], CancellationToken.None, unattended: mode);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        return (result, host.Sessions.Single());
    }

    [Fact]
    public async Task Stored_credentials_and_totp_sign_in_by_themselves()
    {
        var host = new FakeBrowserHost { User = SignInPage(CodePage) };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.SignIn);
        Assert.True(result.Success, result.Error);
        Assert.Equal(6, page.Filled["#code"].Length);
    }

    [Fact]
    public async Task A_code_page_without_a_stored_secret_stops_without_entering_anything()
    {
        var host = new FakeBrowserHost { User = SignInPage(CodePage) };
        var (result, page) = await RunAsync(host, Spec(totp: false), WebLoginUnattended.SignIn);
        Assert.True(result.NeedsUser);
        Assert.False(page.Filled.ContainsKey("#code"));
        Assert.DoesNotContain("#verify", page.Clicked);
    }

    [Fact]
    public async Task A_captcha_instead_of_the_form_stops_before_anything_is_typed()
    {
        var host = new FakeBrowserHost { User = _ => Task.CompletedTask };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.SignIn);
        Assert.True(result.NeedsUser);
        Assert.Empty(page.Filled);
        Assert.Empty(page.Clicked);
    }

    [Fact]
    public async Task A_security_check_page_stops_for_the_owner()
    {
        var host = new FakeBrowserHost
        {
            User = SignInPage(p =>
            {
                p.Url = new Uri("https://pay.example/authflow/confirm-its-you");
                lock (p.Visible) p.Visible.Clear();
                p.OnCheck = _ => new WebLoginCheck(false, "Confirm it's you.");
            }),
        };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.SignIn);
        Assert.True(result.NeedsUser);
        Assert.Equal(["#go"], page.Clicked);
    }

    [Fact]
    public async Task An_unknown_page_stops_once_the_unattended_time_is_up()
    {
        var host = new FakeBrowserHost { User = SignInPage(p => p.Url = new Uri("https://pay.example/somewhere")) };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.SignIn);
        Assert.True(result.NeedsUser);
        Assert.Equal(["#go"], page.Clicked);
    }

    [Fact]
    public async Task An_attended_sign_in_the_app_answers_itself_shows_a_page_that_makes_no_progress()
    {
        // After submitting, the bank stays on its sign-in page with a check the app does not recognise (a captcha).
        var host = new FakeBrowserHost
        {
            User = SignInPage(p =>
            {
                lock (p.Visible) p.Visible.Clear();
                p.OnCheck = _ => new WebLoginCheck(true, "");
            }),
        };
        var service = new WebLoginService(host, new WebLoginOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            SettleDelay = TimeSpan.Zero,
            FormTimeout = TimeSpan.FromMilliseconds(300),
            HiddenStuckAfter = TimeSpan.FromMilliseconds(300),
        }, TimeProvider.System, NullLoggerFactory.Instance);
        await using var session = await service.StartAsync("user", Spec(totp: true), [], CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.Mode != WebLoginMode.Interactive && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(WebLoginMode.Interactive, session.Mode);
        Assert.Equal(["#go"], host.Sessions.Single().Clicked);
    }

    [Fact]
    public async Task A_rejected_password_is_reported_as_rejected_credentials()
    {
        var host = new FakeBrowserHost
        {
            User = SignInPage(p =>
            {
                lock (p.Visible) p.Texts["#err"] = "Some of your info is wrong.";
            }),
        };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.SignIn);
        Assert.True(result.CredentialsRejected);
        Assert.Equal(["#go"], page.Clicked);
    }

    [Fact]
    public async Task Session_only_reuses_a_valid_session()
    {
        var host = new FakeBrowserHost { User = _ => Task.CompletedTask };
        var (result, page) = await RunAsync(host, Spec(totp: false), WebLoginUnattended.ResumeOnly, [Cookie("s-1")]);
        Assert.True(result.Success, result.Error);
        Assert.Equal([Home], page.Navigations);
    }

    [Fact]
    public async Task Session_only_stops_when_the_session_has_expired_without_opening_the_sign_in_page()
    {
        var host = new FakeBrowserHost { User = _ => Task.CompletedTask, Redirect = u => u == Home ? Start : null };
        var (result, page) = await RunAsync(host, Spec(totp: true), WebLoginUnattended.ResumeOnly, [Cookie("s-1")]);
        Assert.True(result.NeedsUser);
        Assert.Equal([Home], page.Navigations);
        Assert.Empty(page.Filled);
    }
}
