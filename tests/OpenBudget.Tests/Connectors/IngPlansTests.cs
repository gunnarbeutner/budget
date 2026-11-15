using OpenBudget.Connectors;
using OpenBudget.Connectors.Ing;
using Xunit;
using static OpenBudget.Tests.Connectors.IngPlanPages;

namespace OpenBudget.Tests.Connectors;

/// <summary>Reading ING's payout plans and savings plans from the web banking's plan overviews. All names, IBANs and amounts are made up.</summary>
public sealed class IngPlansTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private const string Savings1 = "DE00 0000 0000 0000 0000 22";
    private const string Reference = "DE00000000000000000011";

    [Fact]
    public void Payout_plans_are_read_by_the_header_columns_and_leave_the_account()
    {
        var html = Payouts("Rücklagen", Savings1, [
            new("Erika Mustermann", "Auszahlung", "01.12.2026", "Monatlich", "1.900,00"),
            new("Erika Mustermann", "Urlaub", "15.10.2026", "Vierteljährlich", "250,50"),
        ], Menu("./auszahlplaene?x=p1"));

        Assert.Equal("DE00000000000000000022", IngHtml.HeaderIban(html));
        var rows = IngHtml.PlanTable(html, incoming: false, Reference, Today)!;
        Assert.Equal(["./auszahlplan_details?x=d0", "./auszahlplan_details?x=d1"], rows.Select(r => r.DetailsHref));
        var plans = rows.Select(r => r.Plan).ToList();
        Assert.Equal(2, plans.Count);
        var p = plans[0];
        Assert.Equal(ScheduledPaymentKind.Standing, p.Kind);
        Assert.Equal((new DateOnly(2026, 12, 1), -1900m, ScheduleUnit.Month, 1, 1), (p.NextDate, p.Amount, p.IntervalUnit!.Value, p.IntervalCount!.Value, p.ExecutionDay!.Value));
        Assert.Equal(("Erika Mustermann", "Auszahlung", Reference), (p.CounterpartyName, p.Purpose, p.CounterpartyIban));
        // a date already past moves on by the rhythm
        Assert.Equal((new DateOnly(2027, 1, 15), -250.50m, 3), (plans[1].NextDate, plans[1].Amount, plans[1].IntervalCount!.Value));
        Assert.NotEqual(plans[0].BankOrderId, plans[1].BankOrderId);
        Assert.StartsWith("web:", p.BankOrderId, StringComparison.Ordinal);
    }

    [Fact]
    public void Savings_plans_pay_into_the_account()
    {
        var html = Savings("Rücklagen", Savings1, [new("Erika Mustermann", "Sparen", "03.12.2026", "Monatlich", "75,00")]);
        var plan = Assert.Single(IngHtml.PlanTable(html, incoming: true, Reference, Today)!).Plan;
        Assert.Equal((75m, "Erika Mustermann", "Sparen", 3), (plan.Amount, plan.CounterpartyName, plan.Purpose, plan.ExecutionDay!.Value));
    }

    [Fact]
    public void An_empty_overview_says_so_and_another_page_is_not_understood()
    {
        var empty = Savings("Rücklagen", Savings1, []);
        Assert.Null(IngHtml.PlanTable(empty, true, null, Today));
        Assert.True(IngHtml.SaysNoPlans(empty));
        Assert.True(IngHtml.SaysNoPlans(Payouts("Rücklagen", Savings1, [])));
        const string other = "<html><body><h1>Wartungsarbeiten</h1></body></html>";
        Assert.Null(IngHtml.PlanTable(other, false, null, Today));
        Assert.False(IngHtml.SaysNoPlans(other));
    }

    [Fact]
    public void The_detail_page_adds_the_recipient_IBAN_and_leaves_an_open_ended_plan_without_end()
    {
        var row = new Row("Erika Mustermann", "Auszahlung", "01.12.2026", "Monatlich", "1.900,00");
        var overview = IngHtml.PlanTable(Payouts("Rücklagen", Savings1, [row]), false, Reference, Today)![0].Plan;
        var details = Details("Auszahlplan Details", "Rücklagen", Savings1, row);

        var fields = IngHtml.DetailFields(details);
        Assert.Equal(("ING-DiBa AG", "", ""), (fields["Bank"], fields["Letzte Ausführung"], fields["Referenz"]));
        var plan = IngHtml.WithDetails(overview, details, incoming: false, Today)!;
        Assert.Equal(("DE00000000000000000000", "Erika Mustermann", -1900m), (plan.CounterpartyIban, plan.CounterpartyName, plan.Amount));
        Assert.Equal((new DateOnly(2026, 12, 1), (DateOnly?)null, ScheduleUnit.Month, 1), (plan.NextDate, plan.LastDate, plan.IntervalUnit!.Value, plan.IntervalCount!.Value));
        Assert.Equal(overview.BankOrderId, plan.BankOrderId);
    }

    [Fact]
    public void The_detail_page_ends_a_plan_on_its_last_execution()
    {
        var row = new Row("Erika Mustermann", "Sparen", "05.12.2026", "Vierteljährlich", "120,00", "DE00 0000 0000 0000 0000 55", "05.06.2027");
        var overview = IngHtml.PlanTable(Savings("Rücklagen", Savings1, [row]), true, Reference, Today)![0].Plan;
        var plan = IngHtml.WithDetails(overview, Details("Sparauftrag Details", "Rücklagen", Savings1, row), incoming: true, Today)!;
        Assert.Equal((new DateOnly(2027, 6, 5), "DE00000000000000000055", 120m, 3), (plan.LastDate!.Value, plan.CounterpartyIban, plan.Amount, plan.IntervalCount!.Value));

        // a page without plan details is not taken
        Assert.Null(IngHtml.WithDetails(overview, "<html><body><h1>Wartungsarbeiten</h1></body></html>", true, Today));
    }

    [Theory]
    [InlineData("Monatlich", ScheduleUnit.Month, 1)]
    [InlineData("halbjährlich", ScheduleUnit.Month, 6)]
    [InlineData("Jährlich", ScheduleUnit.Month, 12)]
    [InlineData("Wöchentlich", ScheduleUnit.Week, 1)]
    [InlineData("14-tägig", ScheduleUnit.Week, 2)]
    [InlineData("alle 4 Wochen", ScheduleUnit.Week, 4)]
    public void Rhythm_texts_are_understood(string text, ScheduleUnit unit, int count) =>
        Assert.Equal((unit, count), IngHtml.Rhythm(text));

    [Fact]
    public void Links_and_reference_accounts_are_found()
    {
        var menu = Menu("./auszahlplaene?x=p1", "../zahlungsverkehr/sparauftraege?x=s1");
        Assert.Equal("./auszahlplaene?x=p1", IngHtml.LinkByText(menu, "Auszahlpläne"));
        Assert.Equal(["../zahlungsverkehr/sparauftraege?x=s1"], IngHtml.LinksTo(menu, "sparauftraege"));

        var settings = Settings(
            ("Rücklagen", Savings1, "DE00 0000 0000 0000 0000 11", "./sparauftraege?x=a"),
            ("Extra-Konto", "DE00 0000 0000 0000 0000 33", "DE00 0000 0000 0000 0000 44", "./sparauftraege?x=b"));
        var refs = IngHtml.ReferenceAccounts(settings);
        Assert.Equal(Reference, refs["DE00000000000000000022"]);
        Assert.Equal("DE00000000000000000044", refs["DE00000000000000000033"]);
        Assert.Equal(2, IngHtml.LinksTo(settings, "sparauftraege").Count);
    }
}
