using OpenBudget.Connectors;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// Orders paid in instalments: a plan's progress (count, paid, next expected day), complete and cancelled plans in the
/// receipt list, and the instalments still due in the Scheduled view and the cashflow. All data is made up.
/// </summary>
public sealed class InstalmentPlanTests
{
    private static readonly DateTime Day = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    private static InstalmentCharge Charge(long id, int days, long cents, bool matched = true) => new(id, Day.AddDays(days), cents, matched);

    // ---------------------------------------------------------------- progress

    [Fact]
    public void Two_of_three_instalments_give_the_count_the_paid_amount_and_the_next_day()
    {
        var p = InstalmentProgress.Of(27900, [Charge(1, 0, 9300), Charge(2, 31, 9300)], []);
        Assert.Equal((3, 2, 18600L, false, false), (p.Count, p.Charged, p.PaidCents, p.Complete, p.Cancelled));
        Assert.Equal(Day.AddDays(62), p.NextDate);
        Assert.Equal(new InstalmentDue(3, Day.AddDays(62), 9300), Assert.Single(p.Due));
    }

    [Fact]
    public void One_instalment_expects_the_next_a_month_later()
    {
        var p = InstalmentProgress.Of(27900, [Charge(1, 0, 9300)], []);
        Assert.Equal([Day.AddMonths(1), Day.AddMonths(2)], p.Due.Select(d => d.Date));
    }

    [Fact]
    public void All_instalments_charged_and_matched_complete_the_plan()
    {
        var p = InstalmentProgress.Of(27900, [Charge(1, 0, 9300), Charge(2, 31, 9300), Charge(3, 61, 9300)], []);
        Assert.True(p.Complete);
        Assert.Empty(p.Due);
        Assert.False(InstalmentProgress.Of(27900, [Charge(1, 0, 9300), Charge(2, 31, 9300), Charge(3, 61, 9300, matched: false)], []).Complete);
    }

    [Fact]
    public void Rounding_cents_do_not_change_the_count()
    {
        Assert.Equal(3, InstalmentProgress.Of(27900, [Charge(1, 0, 9300)], []).Count);
        var uneven = InstalmentProgress.Of(22499, [Charge(1, 0, 7500), Charge(2, 30, 7499)], []);
        Assert.Equal(3, uneven.Count);
        Assert.Equal(7500, Assert.Single(uneven.Due).Cents);
        Assert.True(InstalmentProgress.Of(22499, [Charge(1, 0, 7500), Charge(2, 30, 7499), Charge(3, 60, 7500)], []).Complete);
        // the last due instalment takes the remainder
        Assert.Equal([7499L, 7500L], InstalmentProgress.Of(22499, [Charge(1, 0, 7500)], []).Due.Select(d => d.Cents));
    }

    [Fact]
    public void Refunds_of_the_charged_instalments_or_of_the_full_price_cancel_the_plan()
    {
        Assert.True(InstalmentProgress.Of(6246, [Charge(1, 0, 2082)], [2082]).Cancelled);
        Assert.True(InstalmentProgress.Of(6246, [Charge(1, 0, 2082)], [6246]).Cancelled);
        var partial = InstalmentProgress.Of(6246, [Charge(1, 0, 2082), Charge(2, 30, 2082)], [2082]);
        Assert.False(partial.Cancelled);
        Assert.Empty(InstalmentProgress.Of(6246, [Charge(1, 0, 2082)], [2082, 6246, 6246]).Due);
    }

    // ---------------------------------------------------------------- receipt list

    private const string Card = "visa-3333";

    private static ReceiptInvoice Receipt(string owner, int login, string order, long cents, DateTime day, ReceiptMatchState state, long? booking = null, string? key = null) => new()
    {
        OwnerUserId = owner,
        Shop = ReceiptShops.Amazon,
        BankLoginId = login,
        SourceKey = key ?? order + "-" + cents,
        InvoiceNumber = key ?? order + "-" + cents,
        OrderNumber = order,
        PaymentMethodId = Card,
        TotalCents = Math.Abs(cents),
        PaymentCents = cents,
        DeliveryDate = day,
        ReadAt = day,
        State = state,
        TransactionId = booking,
    };

    private static ReceiptService Receipts(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System, TestRegistry.Connectors);
    }

    private static async Task<int> LoginAsync(TestDb t, string owner, long? account)
    {
        await using var db = await t.System.CreateDbContextAsync();
        var login = new BankLogin { OwnerUserId = owner, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa •••• 3333", EnrichAccountId = account });
        await db.SaveChangesAsync();
        return login.Id;
    }

    [Fact]
    public async Task A_running_plan_shows_its_progress_and_a_cancelled_one_and_its_reversal_are_done()
    {
        await using var t = await AccessWorld.BuildAsync();
        var login = await LoginAsync(t, AccessWorld.Alice, AccessWorld.AccountWp);
        long running, cancelled, reversal, refund;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var r = Receipt(AccessWorld.Alice, login, "302-0000000-0000301", 27900, Day, ReceiptMatchState.Instalments);
            db.ReceiptInvoices.AddRange(r,
                Receipt(AccessWorld.Alice, login, "302-0000000-0000301", 9300, Day, ReceiptMatchState.Matched, key: "r1"),
                Receipt(AccessWorld.Alice, login, "302-0000000-0000301", 9300, Day.AddDays(31), ReceiptMatchState.Matched, key: "r2"));
            var c = Receipt(AccessWorld.Alice, login, "302-0000000-0000302", 6246, Day, ReceiptMatchState.Instalments);
            var rev = Receipt(AccessWorld.Alice, login, "302-0000000-0000302", -6246, Day.AddDays(10), ReceiptMatchState.Unmatched, key: "c-rev");
            var back = Receipt(AccessWorld.Alice, login, "302-0000000-0000302", -2082, Day.AddDays(10), ReceiptMatchState.Unmatched, key: "c-back");
            db.ReceiptInvoices.AddRange(c, rev, back,
                Receipt(AccessWorld.Alice, login, "302-0000000-0000302", 2082, Day, ReceiptMatchState.Matched, key: "c1"));
            await db.SaveChangesAsync();
            (running, cancelled, reversal, refund) = (r.Id, c.Id, rev.Id, back.Id);
        }
        var rows = await Receipts(t, AccessWorld.Alice).ListAllAsync();

        var plan = rows.Single(r => r.Id == running);
        Assert.Equal(ReceiptReviewState.InstalmentsPending, plan.State);
        Assert.Equal((3, 2, 18600L), (plan.Instalments!.Count, plan.Instalments.Charged, plan.Instalments.PaidCents));
        Assert.Equal(Day.AddDays(62), plan.Instalments.NextDate);

        Assert.Equal(ReceiptReviewState.InstalmentsCancelled, rows.Single(r => r.Id == cancelled).State);
        Assert.Equal(ReceiptReviewState.InstalmentsCancelled, rows.Single(r => r.Id == reversal).State);
        Assert.Equal(ReceiptReviewGroup.Done, ReceiptService.GroupOf(ReceiptReviewState.InstalmentsCancelled, 6246));
        Assert.Equal(ReceiptReviewGroup.Done, ReceiptService.GroupOf(ReceiptReviewState.InstalmentsCancelled, -6246));
        Assert.False(ReceiptService.NeedsReview(rows.Single(r => r.Id == reversal)));
        // the refund of the charged instalment moved money: it still looks for its booking
        Assert.NotEqual(ReceiptReviewState.InstalmentsCancelled, rows.Single(r => r.Id == refund).State);

        var view = await Receipts(t, AccessWorld.Alice).GetReceiptAsync(running);
        Assert.Equal(2, view!.Plan!.Charges.Count);
        Assert.Single(view.Plan.Due);
    }

    // ---------------------------------------------------------------- scheduled and cashflow

    private static async Task<long> PlanAsync(TestDb t, int login, int charged, bool cancelledPlan = false)
    {
        await using var db = await t.System.CreateDbContextAsync();
        var full = Receipt(AccessWorld.Alice, login, "302-0000000-0000401", 27900, Day, ReceiptMatchState.Instalments);
        db.ReceiptInvoices.Add(full);
        for (var i = 0; i < charged; i++)
            db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, login, "302-0000000-0000401", 9300, Day.AddDays(30 * i), ReceiptMatchState.Matched, key: "p" + i));
        if (cancelledPlan) db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, login, "302-0000000-0000401", -27900, Day.AddDays(40), ReceiptMatchState.Unmatched, key: "rev"));
        await db.SaveChangesAsync();
        return full.Id;
    }

    private static readonly DateOnly Today = new(2026, 10, 10);

    [Fact]
    public async Task A_running_plan_lists_its_remaining_instalment_on_the_mapped_account()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var login = await LoginAsync(t, AccessWorld.Alice, AccessWorld.AccountWp);
        var full = await PlanAsync(t, login, 2);

        foreach (var user in new[] { AccessWorld.Alice, AccessWorld.Bob })
        {
            var item = Assert.Single((await new ScheduledPaymentService(t.ForUser(user).Factory, t.System).GetAsync(AccessWorld.Shared, Today)).Items);
            Assert.Equal((ScheduledPaymentKind.Instalment, -93.00m, AccessWorld.AccountWp, full, 3, 3),
                (item.Kind, item.Amount, item.AccountId, item.ReceiptId, item.InstalmentNumber, item.InstalmentCount));
            Assert.Equal(DateOnly.FromDateTime(Day.AddDays(60)), item.NextDate);
        }
    }

    [Fact]
    public async Task Complete_and_cancelled_plans_list_nothing()
    {
        await using var t = await AccessWorld.BuildAsync();
        var login = await LoginAsync(t, AccessWorld.Alice, AccessWorld.AccountWp);
        await PlanAsync(t, login, 3);
        var service = new ScheduledPaymentService(t.ForUser(AccessWorld.Alice).Factory, t.System);
        Assert.Empty((await service.GetAsync(AccessWorld.Shared, Today)).Items);

        await using var t2 = await AccessWorld.BuildAsync();
        var login2 = await LoginAsync(t2, AccessWorld.Alice, AccessWorld.AccountWp);
        await PlanAsync(t2, login2, 1, cancelledPlan: true);
        Assert.Empty((await new ScheduledPaymentService(t2.ForUser(AccessWorld.Alice).Factory, t2.System).GetAsync(AccessWorld.Shared, Today)).Items);
    }

    [Fact]
    public async Task Users_who_cannot_see_the_mapped_account_see_no_instalments()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var login = await LoginAsync(t, AccessWorld.Alice, AccessWorld.AccountPrivate);
        await PlanAsync(t, login, 1);
        Assert.Equal(2, (await new ScheduledPaymentService(t.ForUser(AccessWorld.Alice).Factory, t.System).GetAsync(AccessWorld.Private, Today)).Items.Count);
        var bob = new ScheduledPaymentService(t.ForUser(AccessWorld.Bob).Factory, t.System);
        Assert.Empty((await bob.GetAsync(AccessWorld.Shared, Today)).Items);
        Assert.Empty((await bob.GetAsync(AccessWorld.BobProject, Today)).Items);
    }

    [Fact]
    public async Task The_cashflow_counts_the_remaining_instalments()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(10, "Giro");
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 10, Scope = AccountScope.Exclusive, InCashflow = true });
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var login = new BankLogin { OwnerUserId = TestDb.DefaultOwner, ConnectorType = "amazon", DisplayName = "Shop" };
            db.BankLogins.Add(login);
            await db.SaveChangesAsync();
            db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa", EnrichAccountId = 10 });
            db.ReceiptInvoices.Add(Receipt(TestDb.DefaultOwner, login.Id, "302-0000000-0000501", 27900, Day, ReceiptMatchState.Instalments));
            db.ReceiptInvoices.Add(Receipt(TestDb.DefaultOwner, login.Id, "302-0000000-0000501", 9300, Day, ReceiptMatchState.Matched, key: "x1"));
            await db.SaveChangesAsync();
        }
        var scheduled = await t.Cashflow.GetScheduledCashflowAsync(1, 2026, Today, []);
        var first = scheduled.Occurrences.First();
        Assert.Equal(2, scheduled.Occurrences.Count(o => o.Kind == ScheduledPaymentKind.Instalment));
        Assert.Equal((DateOnly.FromDateTime(Day.AddMonths(1)) < Today.AddDays(1) ? Today.AddDays(1) : DateOnly.FromDateTime(Day.AddMonths(1)), -93m),
            (first.Date, first.Amount));
    }
}
