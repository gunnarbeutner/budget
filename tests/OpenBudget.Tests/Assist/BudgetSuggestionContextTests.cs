#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Connectors;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>"Suggest budget" with AI: year totals, the household context block and the tool-calling loop (fake AI, synthetic data).</summary>
public sealed class BudgetSuggestionContextTests
{
    private const long Group = 10;
    private const long Interest = 11;
    private const long Leisure = 12;
    private const long Giro = 400;
    private const long Loan = 500;
    private static readonly DateOnly AsOf = new(2026, 11, 1);
    private const string Final = "{\"categories\":[]}";

    [Fact]
    public void Year_totals_cover_full_years_the_current_year_and_the_last_12_months_with_and_without_outliers()
    {
        var bookings = new List<BaselineBooking>();
        var id = 0;
        for (var d = new DateOnly(2023, 11, 5); d < AsOf; d = d.AddMonths(1)) bookings.Add(new(++id, d, 100, "example shop"));
        bookings.Add(new(++id, new DateOnly(2025, 6, 1), 2000, "example travel"));
        bookings.Add(new(++id, new DateOnly(2026, 3, 1), 500, "example travel"));

        var totals = BudgetBaselineMath.Totals(bookings, new HashSet<long> { id }, AsOf);

        Assert.Equal([2024, 2025], totals.FullYears.Select(y => y.Year));
        Assert.Equal(new PeriodTotal(12, 1200, 1200), totals.FullYears[0].Total);
        Assert.Equal(new PeriodTotal(12, 3200, 3200), totals.FullYears[1].Total);
        Assert.Equal((2026, new PeriodTotal(10, 1500, 1000)), (totals.Year, totals.CurrentYear));
        Assert.Equal(new PeriodTotal(12, 1700, 1200), totals.Trailing12);

        // On 1 January there is no current year yet; the outliers come from the baseline.
        var january = BudgetBaselineMath.Compute(bookings, [], 2026, new DateOnly(2026, 1, 1));
        Assert.Null(january.Totals!.CurrentYear);
    }

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        (await t.Db.Users.SingleAsync(u => u.Id == TestDb.DefaultOwner)).DisplayName = "Erika Mustermann";
        t.AddGroup(Group);
        t.AddLeaf(Interest, Group, "Interest");
        t.AddLeaf(Leisure, Group, "Leisure");
        t.AddLeaf(13, Group, "Transfer");
        t.AddProject(1);
        var giro = t.AddAccount(Giro, "Giro");
        giro.Type = 1;
        giro.Balances = "11:2500.000000,3:EUR,";
        var loan = t.AddAccount(Loan, "Dispo Erika Mustermann");
        loan.Type = 4;
        loan.Balances = "11:0.000000,3:EUR,";
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = Giro });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = Loan });
        var id = 1000;
        // The facility: drawn in August, repaid at the end of October; interest booked on the giro meanwhile.
        t.AddTransaction(id++, 13, -150000, new DateTime(2026, 8, 3), accountId: Loan, name: "Drawing");
        t.AddTransaction(id++, 13, 150000, new DateTime(2026, 10, 30), accountId: Loan, name: "Repayment");
        t.AddTransaction(id++, Interest, -100, new DateTime(2026, 8, 31), accountId: Giro, name: "Example Bank Zinsen");
        t.AddTransaction(id++, Interest, -1600, new DateTime(2026, 9, 30), accountId: Giro, name: "Example Bank Zinsen");
        t.AddTransaction(id++, Interest, -3500, new DateTime(2026, 10, 30), accountId: Giro, name: "Example Bank Zinsen");
        // A streaming contract booked January to July 2026, then nothing.
        for (var m = 1; m <= 7; m++)
            t.AddTransaction(id++, Leisure, -1299, new DateTime(2026, m, 4), accountId: Giro, name: "Example Streaming Erika Mustermann");
        // A yearly trip and a monthly club fee.
        for (var y = 2024; y <= 2026; y++)
            t.AddTransaction(id++, Leisure, -240000, new DateTime(y, 7, 15), accountId: Giro, name: $"Example Travel {y}");
        for (var d = new DateTime(2023, 11, 20); d < new DateTime(2026, 11, 1); d = d.AddMonths(1))
            t.AddTransaction(id++, Leisure, -3000, d, accountId: Giro, name: "Example Club");
        t.Db.ScheduledPayments.Add(new ScheduledPayment
        {
            Id = 1,
            OwnerUserId = TestDb.DefaultOwner,
            AccountId = Giro,
            Kind = ScheduledPaymentKind.Standing,
            NextDate = new DateOnly(2026, 11, 20),
            IntervalUnit = ScheduleUnit.Month,
            IntervalCount = 1,
            Amount = -3000,
            CounterpartyName = "Example Club",
            CounterpartyIban = "DE02120300000000202051",
        });
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.UserDataStates.SingleAsync(s => s.UserId == TestDb.DefaultOwner)).UseAiSuggestions = true;
            await db.SaveChangesAsync();
        }
        return t;
    }

    private static BudgetSuggestionService Service(TestDb t, IAssistClient ai)
    {
        var v = t.ForUser(TestDb.DefaultOwner);
        return new BudgetSuggestionService(v.Factory, v.Hierarchy, v.Projects, new AssistSettingsService(v.Factory, t.System, ai), ai,
            NullLogger<BudgetSuggestionService>.Instance, TimeProvider.System);
    }

    private static readonly BudgetSuggestionInput[] Rows =
    [
        new((Interest, CategoryItemType.Category), "Interest", false),
        new((Leisure, CategoryItemType.Category), "Leisure", false),
    ];

    private static AssistResponse Calls(params AssistToolCall[] calls) =>
        new(null, null, "fake", 10, 0, 5, TimeSpan.Zero) { ToolCalls = calls };

    private static JsonObject Context(AssistRequest request)
    {
        var text = request.UserContent;
        var start = text.IndexOf('\n', StringComparison.Ordinal) + 1;
        var end = text.IndexOf("\n\nCategories:", StringComparison.Ordinal);
        return (JsonObject)JsonNode.Parse(text[start..end])!;
    }

    [Fact]
    public async Task The_context_block_has_the_debt_account_balances_ended_contracts_and_schedules_all_redacted()
    {
        await using var t = await WorldAsync();
        var ai = new FakeAssistClient(_ => Final);

        var result = await Service(t, ai).SuggestAsync(1, 2027, Rows, useAi: true, AsOf);

        Assert.True(result.AiUsed);
        var request = Assert.Single(ai.Requests);
        Assert.StartsWith("Household context:\n", request.UserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Mustermann", request.UserContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DE02120300000000202051", request.UserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispo", request.UserContent, StringComparison.Ordinal);
        var context = Context(request);

        var account = Assert.Single(context["debt_accounts"]!.AsArray())!;
        Assert.Equal(("OWN_2", "loan", 0m, true), (account["account"]!.GetValue<string>(), account["kind"]!.GetValue<string>(),
            account["balance_now"]!.GetValue<decimal>(), account["zero_now"]!.GetValue<bool>()));
        var ends = account["month_end_balances"]!.AsObject();
        Assert.Equal(["2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10"], ends.Select(e => e.Key));
        Assert.Equal([0m, 0m, 0m, -1500m, -1500m, 0m], ends.Select(e => e.Value!.GetValue<decimal>()));

        var ended = Assert.Single(context["ended_monthly_payees"]!.AsArray())!;
        Assert.Equal("EXAMPLE STREAMING PERSON_A", ended["payee"]!.GetValue<string>());
        Assert.Equal(("c2", "2026-07-04", 12.99m, 7), (ended["category_id"]!.GetValue<string>(), ended["last"]!.GetValue<string>(),
            ended["usual_amount"]!.GetValue<decimal>(), ended["months_in_a_row"]!.GetValue<int>()));

        var schedule = Assert.Single(context["scheduled_payments"]!.AsArray())!;
        Assert.Equal(("EXAMPLE CLUB", -30m, "monthly", "2026-11-20", "c2"), (schedule["payee"]!.GetValue<string>().ToUpperInvariant(),
            schedule["amount"]!.GetValue<decimal>(), schedule["interval"]!.GetValue<string>(), schedule["next"]!.GetValue<string>(),
            schedule["category_id"]!.GetValue<string>()));

        Assert.Contains("\"year_totals\":{\"full_years\":{\"2024\":", request.UserContent, StringComparison.Ordinal);
        Assert.Contains("\"current_year_to_date\":{\"months\":10,", request.UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_calls_are_answered_redacted_and_unknown_handles_get_an_error()
    {
        await using var t = await WorldAsync();
        var ai = new FakeAssistClient(r => r.Turns is { Count: > 0 } ? new AssistResponse(Final, null, "fake", 10, 0, 5, TimeSpan.Zero) : Calls(
            new("call_1", "category_bookings", "{\"category_id\":\"c2\",\"from_month\":\"2026-01\",\"to_month\":\"2026-03\"}"),
            new("call_2", "payee_bookings", "{\"category_id\":\"c2\",\"payee\":\"example streaming PERSON_A\"}"),
            new("call_3", "category_bookings", "{\"category_id\":\"c9\",\"from_month\":\"2026-01\",\"to_month\":\"2026-03\"}"),
            new("call_4", "account_balance_history", "{\"account\":\"OWN_2\"}"),
            new("call_5", "account_balance_history", "{\"account\":\"OWN_7\"}")));

        var result = await Service(t, ai).SuggestAsync(1, 2027, Rows, useAi: true, AsOf);

        Assert.Null(result.AiError);
        Assert.Equal(2, ai.Requests.Count);
        Assert.All(ai.Requests, r => Assert.Equal(3, r.Tools.Count));
        var turns = ai.Requests[1].Turns!;
        var outputs = turns.Where(n => n!["type"]!.GetValue<string>() == "function_call_output")
            .ToDictionary(n => n!["call_id"]!.GetValue<string>(), n => JsonNode.Parse(n!["output"]!.GetValue<string>())!, StringComparer.Ordinal);
        Assert.Equal(5, outputs.Count);
        Assert.Equal(5, turns.Count(n => n!["type"]!.GetValue<string>() == "function_call"));
        Assert.DoesNotContain("Mustermann", turns.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var range = outputs["call_1"];
        Assert.Equal((6, false), (range["count"]!.GetValue<int>(), range["truncated"]!.GetValue<bool>()));
        Assert.All(range["bookings"]!.AsArray(), b => Assert.Equal("OWN_1", b!["account"]!.GetValue<string>()));
        Assert.Matches("^t\\d+$", range["bookings"]![0]!["tx"]!.GetValue<string>());

        var payee = outputs["call_2"];
        Assert.Equal((7, -90.93m), (payee["count"]!.GetValue<int>(), payee["sum"]!.GetValue<decimal>()));
        Assert.Contains("PERSON_A", payee["bookings"]![0]!["payee"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Contains("unknown category_id", outputs["call_3"]["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(36, outputs["call_4"]["month_end_balances"]!.AsObject().Count);
        Assert.Contains("unknown account", outputs["call_5"]["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_the_round_cap_the_model_must_answer_without_tools()
    {
        await using var t = await WorldAsync();
        var ai = new FakeAssistClient(r => r.ToolsDisabled
            ? new AssistResponse(Final, null, "fake", 10, 0, 5, TimeSpan.Zero)
            : Calls(new AssistToolCall("call", "category_bookings", "{\"category_id\":\"c1\",\"from_month\":\"2026-01\",\"to_month\":\"2026-10\"}")));

        var result = await Service(t, ai).SuggestAsync(1, 2027, Rows, useAi: true, AsOf);

        Assert.Null(result.AiError);
        Assert.Equal(BudgetSuggestionService.MaxToolRounds + 1, ai.Requests.Count);
        Assert.All(ai.Requests.Take(BudgetSuggestionService.MaxToolRounds), r => Assert.False(r.ToolsDisabled));
        Assert.True(ai.Requests[^1].ToolsDisabled);
    }

    [Fact]
    public void The_request_body_lists_the_tools_and_sends_earlier_rounds_back()
    {
        var request = new AssistRequest("x", BudgetSuggestionService.Schema(), "sys", "user")
        {
            Tools = BudgetSuggestionTools.Definitions,
            Turns = [new JsonObject { ["type"] = "function_call_output", ["call_id"] = "a", ["output"] = "{}" }],
            ToolsDisabled = true,
        };

        var body = OpenAiAssistClient.BuildBody("m", request);

        Assert.Equal(3, body["input"]!.AsArray().Count);
        Assert.Equal("none", body["tool_choice"]!.GetValue<string>());
        Assert.All(body["tools"]!.AsArray(), tool => Assert.True(tool!["strict"]!.GetValue<bool>()));
        var parsed = OpenAiAssistClient.Parse("""
            {"output":[{"type":"function_call","call_id":"c","name":"payee_bookings","arguments":"{}"}],"usage":{"input_tokens":5,"output_tokens":1}}
            """, TimeSpan.Zero);
        Assert.Equal(new AssistToolCall("c", "payee_bookings", "{}"), Assert.Single(parsed.ToolCalls));
        Assert.Single(parsed.OutputItems!);
    }
}
