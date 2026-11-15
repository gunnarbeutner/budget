using OpenBudget.Connectors;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Recurring charges detected from the booking history: grouping card descriptors, splitting one payee into series by
/// amount, rhythms, price changes, status, standing orders, and their place in the cashflow forecast. All data is made up.
/// </summary>
public sealed class RecurringPaymentTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly HashSet<string> NoIbans = new(StringComparer.Ordinal);
    private long _id;

    private RecurringBooking B(DateOnly date, long cents, string name, long account = 10, long? category = null, string? iban = null) =>
        new(++_id, account, date, cents, name, null, category, iban);

    /// <summary>Monthly on <paramref name="day"/> for <paramref name="months"/> months ending in <paramref name="lastMonth"/>.</summary>
    private IEnumerable<RecurringBooking> Monthly(string name, long cents, int months, DateOnly lastMonth, int day = 3)
    {
        for (var i = months - 1; i >= 0; i--)
        {
            var m = lastMonth.AddMonths(-i);
            yield return B(new DateOnly(m.Year, m.Month, Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month))), cents, name);
        }
    }

    private static List<RecurringSeries> Detect(IEnumerable<RecurringBooking> bookings, IReadOnlyCollection<StandingOrderKey>? standing = null) =>
        RecurringPaymentDetector.Detect(bookings, standing ?? [], NoIbans, Today);

    [Fact]
    public void Monthly_transfers_to_a_person_of_the_household_are_no_subscription()
    {
        var last = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        var bookings = Monthly("Erika Maria Mustermann", -3569, 6, last).Concat(Monthly("Streamio Media", -1299, 6, last)).ToList();

        var series = RecurringPaymentDetector.Detect(bookings, [], NoIbans, Today, ["Erika Mustermann"]);

        Assert.Equal(["Streamio Media"], series.Select(s => s.Payee));
    }

    [Fact]
    public void A_shop_visited_irregularly_is_no_subscription_even_with_a_regular_run()
    {
        var start = Today.AddMonths(-13);
        var bookings = new List<RecurringBooking>
        {
            B(start, -1067, "Corner Market"), B(start.AddMonths(6), -1106, "Corner Market"), B(start.AddMonths(12), -1387, "Corner Market"),
        };
        foreach (var d in new[] { 9, 40, 75, 130, 200, 250, 290, 330 }) bookings.Add(B(start.AddDays(d), -2345 - d, "Corner Market"));

        Assert.Empty(Detect(bookings));
    }

    [Theory]
    [InlineData("APPLE.COM/BILL", "apple com bill")]
    [InlineData("Apple.com/bill", "apple com bill")]
    [InlineData("Amzn Mktp DE*XYZ123", "amzn mktp de")]
    [InlineData("PayPal Arenanetllc 1049283", "paypal arenanetllc")]
    [InlineData("Straßenbahn Ünterwegs 2026-10", "straßenbahn unterwegs")]
    public void Payees_are_normalised(string raw, string expected) =>
        Assert.Equal(expected, RecurringPaymentDetector.NormalizePayee(raw));

    [Fact]
    public void A_monthly_charge_moved_by_weekends_and_short_months_is_one_active_series()
    {
        // The 31st falls on short months' last day and weekends push some charges by up to three days.
        var dates = new[] { (2026, 4, 30), (2026, 6, 1), (2026, 6, 30), (2026, 8, 3), (2026, 8, 31), (2026, 9, 30), (2026, 11, 2) };
        var series = Assert.Single(Detect(dates.Select(d => B(new DateOnly(d.Item1, d.Item2, d.Item3), -1299, "Streamio Media"))));
        Assert.Equal(RecurringRhythm.Month, series.Rhythm);
        Assert.Equal(RecurringStatus.Active, series.Status);
        Assert.Equal(7, series.Bookings.Count);
        Assert.Equal(-1299, series.MonthlyCents);
        Assert.Null(series.PreviousCents);
    }

    [Fact]
    public void A_yearly_charge_is_found_and_costs_a_twelfth_per_month()
    {
        var series = Assert.Single(Detect([B(new(2023, 3, 10), -6000, "Example Club"), B(new(2024, 3, 12), -6000, "Example Club"), B(new(2025, 3, 8), -6000, "Example Club"), B(new(2026, 3, 11), -6000, "Example Club")]));
        Assert.Equal(RecurringRhythm.Year, series.Rhythm);
        Assert.Equal(-500, series.MonthlyCents);
        Assert.Equal(new DateOnly(2027, 3, 11), series.NextDate);
        Assert.Equal(RecurringStatus.Active, series.Status);
    }

    [Fact]
    public void One_payee_with_several_amounts_gives_one_series_per_amount()
    {
        var last = new DateOnly(2026, 11, 1);
        var bookings = Monthly("APPLE.COM/BILL", -99, 6, last, 2)
            .Concat(Monthly("Apple.com/bill", -299, 5, last, 9))
            .Concat(Monthly("APPLE.COM/BILL", -1499, 4, last, 20))
            .Append(B(new(2026, 7, 14), -5999, "APPLE.COM/BILL")); // a one-off purchase
        var series = Detect(bookings);
        Assert.Equal([-1499L, -299L, -99L], series.Select(s => s.Cents));
        Assert.All(series, s => Assert.Equal(RecurringRhythm.Month, s.Rhythm));
        Assert.Equal(3, series.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_price_change_continues_the_series_and_shows_old_and_new()
    {
        var bookings = Monthly("Example Music", -999, 5, new DateOnly(2026, 7, 1), 5).Concat(Monthly("Example Music", -1199, 4, new DateOnly(2026, 11, 1), 5));
        var series = Assert.Single(Detect(bookings));
        Assert.Equal(9, series.Bookings.Count);
        Assert.Equal((-1199L, -999L, new DateOnly(2026, 8, 5)), (series.Cents, series.PreviousCents!.Value, series.PriceChangedOn!.Value));
    }

    [Fact]
    public void A_series_that_missed_two_charges_has_ended_and_one_that_missed_one_is_overdue()
    {
        var ended = Assert.Single(Detect(Monthly("Old Gym", -2990, 8, new DateOnly(2026, 7, 1))));
        Assert.Equal(RecurringStatus.Ended, ended.Status);
        var overdue = Assert.Single(Detect(Monthly("Example News", -800, 4, new DateOnly(2026, 9, 25), 25)));
        Assert.Equal(RecurringStatus.Overdue, overdue.Status);
    }

    [Fact]
    public void Card_descriptors_with_changing_references_and_wordings_form_one_series()
    {
        var bookings = new[]
        {
            B(new(2026, 7, 4), -1800, "Anthropic Claude SUB"),
            B(new(2026, 8, 4), -1800, "Claude.ai Subscription"),
            B(new(2026, 9, 4), -1800, "CLAUDE.AI SUBSCRIPTION 88812"),
            B(new(2026, 10, 6), -1800, "Anthropic Claude SUB"),
            B(new(2026, 6, 10), -2500, "Exmpl Mktp DE*AB12CD"),
            B(new(2026, 7, 10), -2500, "Exmpl Mktp DE*ZZ98XY"),
            B(new(2026, 8, 11), -2500, "EXMPL MKTP DE*QQ11RR"),
            B(new(2026, 9, 10), -2500, "Exmpl Mktp DE*PP22SS"),
        };
        var series = Detect(bookings);
        Assert.Equal(2, series.Count);
        Assert.Equal(4, series.Single(s => s.Cents == -1800).Bookings.Count);
        Assert.Equal(4, series.Single(s => s.Cents == -2500).Bookings.Count);
    }

    [Fact]
    public void A_charge_the_bank_reports_as_a_standing_order_is_left_out()
    {
        var bookings = Monthly("Example Landlord", -85000, 6, new DateOnly(2026, 11, 1), 1).ToList();
        Assert.Single(Detect(bookings));
        Assert.Empty(Detect(bookings, [new StandingOrderKey(10, -85000, "Example Landlord GmbH", null)]));
        Assert.Single(Detect(bookings, [new StandingOrderKey(11, -85000, "Example Landlord GmbH", null)])); // another account
    }

    [Fact]
    public void Too_few_or_irregular_charges_and_transfers_between_own_accounts_are_no_series()
    {
        Assert.Empty(Detect(Monthly("Example Insurance", -4500, 2, new DateOnly(2026, 11, 1))));
        Assert.Empty(Detect([B(new(2026, 8, 1), -3000, "Example Bakery"), B(new(2026, 8, 19), -3000, "Example Bakery"), B(new(2026, 10, 30), -3000, "Example Bakery")]));
        var savings = Monthly("Savings", -20000, 6, new DateOnly(2026, 11, 1)).Select(b => b with { CounterpartyIban = "DE00 0000 0000 0000 0000 55" });
        Assert.Empty(RecurringPaymentDetector.Detect(savings, [], new HashSet<string>(["DE00000000000000000055"], StringComparer.Ordinal), Today));
        Assert.Empty(Detect(Monthly("Example Employer", 300000, 6, new DateOnly(2026, 11, 1)))); // income
    }

    [Fact]
    public void The_expected_charges_follow_the_last_one()
    {
        var series = Assert.Single(Detect(Monthly("Example Music", -999, 4, new DateOnly(2026, 10, 31), 31)));
        Assert.Equal(
            [new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 31)],
            series.Occurrences(Today, new DateOnly(2027, 1, 31)));
        // a charge a few days late is expected right away
        Assert.Equal(new DateOnly(2026, 12, 3), series.Occurrences(new DateOnly(2026, 12, 3), new DateOnly(2026, 12, 31)).First());
    }

    // ---------------------------------------------------------------- service and cashflow

    private const long Group = 100, Media = 101, Other = 102;

    private async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        t.AddGroup(Group);
        t.AddLeaf(Media, Group, "Media");
        t.AddLeaf(Other, Group, "Other");
        t.AddProject(1);
        t.TrackCategory(1, Media);
        t.AddAccount(10, "Giro");
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 10, Scope = AccountScope.Exclusive, InCashflow = true });
        var id = 0L;
        foreach (var b in Monthly("Streamio Media", -1500, 6, new DateOnly(2026, 11, 1), 1))
            t.Db.Transactions.Add(new Transaction
            {
                Id = ++id,
                OwnerUserId = TestDb.DefaultOwner,
                AccountId = 10,
                CategoryId = Media,
                Amount = b.Cents,
                Name = b.Name,
                Timestamp = new DateTimeOffset(b.Date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds(),
            });
        await t.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task The_project_lists_its_series_and_Scheduled_shows_the_next_charge()
    {
        await using var t = await WorldAsync();
        var recurring = new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects);
        var entry = Assert.Single(await recurring.GetAsync(1, Today));
        Assert.Equal(("Streamio Media", "Media101", false), (entry.Series.Payee, entry.CategoryName, entry.Dismissed));

        var scheduled = await new ScheduledPaymentService(t.Factory, recurring: recurring).GetAsync(1, Today);
        var item = Assert.Single(scheduled.Items);
        Assert.Equal((ScheduledPaymentKind.Recurring, new DateOnly(2026, 12, 1), -15m), (item.Kind, item.NextDate, item.Amount));
    }

    [Fact]
    public async Task A_dismissed_series_stays_listed_as_dismissed_and_leaves_Scheduled()
    {
        await using var t = await WorldAsync();
        var user = t.ForUser(TestDb.DefaultOwner);
        var recurring = new RecurringPaymentService(user.Factory, user.Hierarchy, user.Projects, user.Access);
        var entry = Assert.Single(await recurring.GetAsync(1, Today));
        await recurring.SetDismissedAsync(entry.Series.Key, true);

        Assert.True(Assert.Single(await recurring.GetAsync(1, Today)).Dismissed);
        Assert.Empty(await recurring.ActiveAsync(1, Today));
        await recurring.SetDismissedAsync(entry.Series.Key, false);
        Assert.Single(await recurring.ActiveAsync(1, Today));
    }

    [Fact]
    public async Task The_forecast_books_a_series_on_its_days_and_takes_it_out_of_the_category_projection()
    {
        await using var t = await WorldAsync();
        var cashflow = new CashflowProjectionService(t.Factory, t.Hierarchy, t.Projects, t.System, new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects));
        var row = (Media, CategoryItemType.Category);
        var scheduled = await cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [row]);

        Assert.Equal(12, scheduled.Occurrences.Count);
        Assert.All(scheduled.Occurrences, o => Assert.Equal((ScheduledPaymentKind.Recurring, -15m), (o.Kind, o.Amount)));
        Assert.All(scheduled.CoveredByRow[row], m => Assert.Equal(-15m, m));

        // History projects 15 a month for Media; the series pays exactly that, so the year costs 12 × 15, not 24 × 15.
        var expenses = new List<CategoryCashflow>
        {
            new() { CategoryId = Media, ItemType = CategoryItemType.Category, MonthlyAmounts = Enumerable.Repeat(15m, 12).ToArray(), IsAggregateRow = true },
        };
        var histograms = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution> { [row] = DayOfMonthDistribution.Day1Fallback() };
        var data = new ProjectDailyDeltaResult { HasAccounts = true, StartingBalance = 1000m, DailyDelta = new decimal[365], LastActualDayOfYear = 0 };
        var result = CashflowCalculator.ComputeBalance(data, 2027, 2026, 1, [], expenses, histograms, [], 0m, scheduled);
        Assert.Equal(1000m - 12 * 15m, result.BalanceDaily[^1]);
    }
}
