using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;
using OpenBudget.Tests.Receipts;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The mailbox card on the connections page against the fake Microsoft: mail access starts as not granted, "Grant"
/// signs in asking for Mail.Read, "Check mail now" reads a made-up invoice mail, and the project the mailbox pays from lists the
/// receipt until its booking arrives and opens it in the receipt panel.
/// </summary>
[TestFixture]
public sealed class ReceiptsSettingsTests : EndToEndTestBase
{
    private FakeMicrosoftCloud Microsoft => Factory.Microsoft;

    [Test]
    public async Task The_mailbox_card_is_sorted_and_spaced_like_the_logins()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.BankLogins.Add(new BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "amazon", DisplayName = "Alpha Example Shop", RefreshInterval = RefreshInterval.ManualOnly });
            db.BankLogins.Add(new BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "amazon", DisplayName = "Zulu Example Shop", RefreshInterval = RefreshInterval.ManualOnly });
            await db.SaveChangesAsync();
        }

        await GoToAsync("/connections");
        await Expect(Page.GetByTestId("mailbox-card")).ToBeVisibleAsync();
        var titles = await Page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('.bank-login-list > .card')].map(c => c.querySelector('h5').textContent.trim())");
        Assert.That(titles, Does.Contain("Mailbox (Microsoft 365)"));
        Assert.That(titles, Is.EqualTo(titles.Order(StringComparer.Ordinal).ToArray()));
        var smallestGap = await Page.EvaluateAsync<double>(
            "() => { const r = [...document.querySelectorAll('.bank-login-list > .card')].map(c => c.getBoundingClientRect()); return Math.min(...r.slice(1).map((b, i) => b.top - r[i].bottom)); }");
        Assert.That(smallestGap, Is.GreaterThan(10));
    }

    [Test]
    public async Task Grant_mail_access_check_mail_and_list_the_receipt()
    {
        await ReceiptsSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>(), ReceiptSources.Mail);
        Microsoft.AccountName = "e2e@example.org";
        Microsoft.AddMail(new("mail-99", "Deine Rechnung zu B-AAA-BBB-C99", DateTime.UtcNow.AddDays(-1),
            [("att-1", "Rechnung_PN10000000000099.pdf", SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), invoice: "PN10000000000099", order: "B-AAA-BBB-C99"))]));
        await Page.RouteAsync("**/connect/onedrive?return=receipts", async route =>
        {
            var response = await route.FetchAsync(new() { MaxRedirects = 0 });
            var location = new Uri(response.Headers["location"]);
            Assert.That(Uri.UnescapeDataString(location.Query), Does.Contain("Mail.Read"));
            var (code, state, redirect) = Microsoft.Authorize(location);
            await route.FulfillAsync(new()
            {
                Status = 302,
                Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Location"] = $"{redirect}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}",
                },
            });
        });

        await GoToAsync("/connections");
        await Expect(Page.GetByTestId("receipts-mail-status")).ToContainTextAsync("not granted");

        await Page.GetByTestId("receipts-mail-grant").ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("/connections?onedrive=connected", StringComparison.Ordinal));
        await WaitForInteractiveAsync();
        await Expect(Page.GetByTestId("receipts-notice")).ToContainTextAsync("Mail access granted");
        await Expect(Page.GetByTestId("receipts-mail-status")).ToContainTextAsync("e2e@example.org");

        await Page.GetByTestId("receipts-mail-check").ClickAsync();
        await Expect(Page.GetByTestId("receipts-notice")).ToContainTextAsync("1 new receipts");
        await Expect(Page.GetByTestId("mailbox-summary")).ToContainTextAsync("receipts read");
        await Page.GetByTestId("receipts-mail-stop").ClickAsync();
        await Expect(Page.GetByTestId("receipts-mail-grant")).ToBeVisibleAsync();

        await GoToAsync(ReceiptsSeed.ReceiptsPath);
        // Other receipt tests may have added rows of their own: this test's row is found by its order number.
        var row = Page.GetByTestId("receipt-row").Filter(new() { HasText = "B-AAA-BBB-C99" });
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row).ToHaveAttributeAsync("data-state", new System.Text.RegularExpressions.Regex("^(waiting|unmatched)$"));
        await Expect(row.GetByTestId("receipt-assign")).ToBeVisibleAsync();

        await row.GetByTestId("receipt-open").ClickAsync();
        var panel = Page.GetByTestId("receipt-panel");
        await Expect(panel.GetByTestId("receipt-panel-line").Filter(new() { HasText = "Apfelschorle" })).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("receipt-panel-paid")).ToContainTextAsync("15,28");
    }
}
