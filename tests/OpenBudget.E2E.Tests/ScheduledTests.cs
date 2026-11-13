using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Scheduled view: the banks' standing orders and future-dated transfers of the project's accounts, soonest first,
/// with the kind and account filters, the empty state, and the phone layout.
/// </summary>
[TestFixture]
public sealed class ScheduledTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private ILocator Rows => Page.GetByTestId("scheduled-row");

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The editing world plus: a monthly rent order and a one-off transfer on the bank account, a quarterly order on the
    /// MoneyMoney account, and an order on an account outside the project (never shown).
    /// </summary>
    private async Task SeedOrdersAsync()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        var outside = RowIds.Namespace(E2ESeed.IdSpace, 9);
        db.Accounts.Add(new Account { Id = outside, OwnerUserId = E2ESeed.OwnerUserId, Name = "Outside Account", Type = 1, Currency = "EUR" });
        await db.SaveChangesAsync();
        var next = Today.AddDays(5);
        db.ScheduledPayments.AddRange(
            Order(EditingSeed.BankAccountId, ScheduledPaymentKind.Standing, next, -850_00, "Example Landlord", "Rent flat 3", monthDay: next.Day),
            Order(EditingSeed.BankAccountId, ScheduledPaymentKind.Scheduled, Today.AddDays(2), -49_99, "Example Shop", "Order 4711"),
            Order(EditingSeed.MoneyMoneyAccountId, ScheduledPaymentKind.Standing, Today.AddDays(20), 300_00, "Example Tenant", "Garage rent", monthDay: Today.AddDays(20).Day, interval: 3, last: new DateOnly(2027, 12, 31)),
            Order(outside, ScheduledPaymentKind.Standing, Today.AddDays(1), -10_00, "Hidden Payee", "not in the project", monthDay: 1));
        await db.SaveChangesAsync();
    }

    private static ScheduledPayment Order(long account, ScheduledPaymentKind kind, DateOnly next, long cents, string name, string purpose,
        int? monthDay = null, int interval = 1, DateOnly? last = null) => new()
        {
            OwnerUserId = E2ESeed.OwnerUserId,
            AccountId = account,
            Kind = kind,
            NextDate = next,
            FirstDate = kind == ScheduledPaymentKind.Standing ? next : null,
            LastDate = last,
            IntervalUnit = kind == ScheduledPaymentKind.Standing ? ScheduleUnit.Month : null,
            IntervalCount = kind == ScheduledPaymentKind.Standing ? interval : null,
            ExecutionDay = monthDay,
            Amount = cents,
            CounterpartyName = name,
            CounterpartyIban = "DE00000000000000000099",
            CounterpartyBic = "TESTDEFFXXX",
            Purpose = purpose,
            FetchedAt = DateTime.UtcNow,
        };

    [Test]
    public async Task The_view_lists_the_projects_orders_soonest_first_and_filters_by_kind_and_account()
    {
        await SeedOrdersAsync();
        await GoToAsync($"/projects/{Pid}/browse");
        await Page.GetByTestId("project-views").GetByTestId("view-scheduled").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/scheduled$"));
        await Expect(Page.GetByTestId("view-scheduled")).ToHaveAttributeAsync("aria-current", "page");

        await Expect(Rows).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("scheduled-name")).ToHaveTextAsync(new[] { "Example Shop", "Example Landlord", "Example Tenant" });
        var shop = Rows.Nth(0);
        await Expect(shop.GetByTestId("scheduled-in")).ToHaveTextAsync("in 2 days");
        await Expect(shop.GetByTestId("scheduled-rhythm")).ToHaveTextAsync("Once");
        await Expect(shop.GetByTestId("scheduled-amount")).ToHaveTextAsync("−49,99 €");
        await Expect(shop.GetByTestId("scheduled-amount")).ToHaveClassAsync(new Regex("money-neg"));
        await Expect(shop.GetByTestId("scheduled-iban")).ToHaveTextAsync("DE00 0000 0000 0000 0000 99 · TESTDEFFXXX");
        await Expect(shop.GetByTestId("scheduled-account-name")).ToHaveTextAsync(EditingSeed.BankAccountName);
        await Expect(Rows.Nth(1).GetByTestId("scheduled-rhythm")).ToHaveTextAsync(new Regex(@"^Monthly on the \d+(st|nd|rd|th) · ∞$"));
        await Expect(Rows.Nth(2).GetByTestId("scheduled-rhythm")).ToHaveTextAsync(new Regex(@"^Quarterly on the .* · until 31\.12\.2027$"));
        await Expect(Rows.Nth(2).GetByTestId("scheduled-amount")).ToHaveClassAsync(new Regex("money-pos"));
        await Expect(Page.GetByText("Hidden Payee")).ToHaveCountAsync(0);

        await Page.GetByTestId("scheduled-kind").SelectOptionAsync("standing");
        await Expect(Page).ToHaveURLAsync(new Regex("kind=standing"));
        await Expect(Page.GetByTestId("scheduled-name")).ToHaveTextAsync(new[] { "Example Landlord", "Example Tenant" });

        await Page.GetByTestId("scheduled-account").SelectOptionAsync(EditingSeed.MoneyMoneyAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Expect(Page.GetByTestId("scheduled-name")).ToHaveTextAsync(new[] { "Example Tenant" });

        await Page.GetByTestId("scheduled-kind").SelectOptionAsync("scheduled");
        await Expect(Rows).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("scheduled-empty")).ToHaveTextAsync("Nothing matches the filters.");

        // The filters live in the URL: a reload keeps them.
        await Page.ReloadAsync();
        await WaitForInteractiveAsync();
        await Expect(Page.GetByTestId("scheduled-kind")).ToHaveValueAsync("scheduled");
        await Expect(Rows).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Without_orders_the_view_explains_where_they_come_from()
    {
        await GoToAsync($"/projects/{Pid}/scheduled");
        await Expect(Page.GetByTestId("scheduled-empty")).ToContainTextAsync("FinTS");
    }

    [Test]
    public async Task The_list_fits_a_phone()
    {
        await SeedOrdersAsync();
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/scheduled");
        await Expect(Rows).ToHaveCountAsync(3);
        await Expect(Rows.First.GetByTestId("scheduled-amount")).ToBeInViewportAsync();
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        Assert.That(overflow, Is.False);
    }
}
