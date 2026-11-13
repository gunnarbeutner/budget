using System.Text.Json.Nodes;
using OpenBudget.Assist;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Contract keys: mandate, creditor, payee and recurring amount in the matching and in what the AI sees (synthetic data).</summary>
public sealed class ContractMatchingTests
{
    private const long Electricity = 1, Gas = 2, Streaming = 3, Apps = 4, Insurance = 5;

    private static readonly DateOnly Start = new(2026, 1, 15);

    private static HistoryBooking H(long id, long category, string payee, string purpose, long amount, string? mandate = null, string? creditor = null, int month = 0) =>
        new(id, category, null, CategoryMatching.PayeeKey(payee), CategoryMatching.Tokens(payee, purpose), amount)
        {
            Mandate = CategoryMatching.SepaKey(mandate),
            Creditor = CategoryMatching.SepaKey(creditor),
            Date = Start.AddMonths(month),
        };

    private static TargetBooking T(string payee, string purpose, long amount, string? mandate = null, string? creditor = null, int month = 10) =>
        new(999, null, CategoryMatching.PayeeKey(payee), CategoryMatching.Tokens(payee, purpose), amount)
        {
            Mandate = CategoryMatching.SepaKey(mandate),
            Creditor = CategoryMatching.SepaKey(creditor),
            Date = Start.AddMonths(month),
        };

    [Fact]
    public void The_same_mandate_beats_closer_text()
    {
        var m = new CategoryMatching(
        [
            H(1, Electricity, "Example Energy", "Abschlag Strom Vertrag", -8000, "MD-POWER-01", "XX00ZZZ0000001", 1),
            H(2, Electricity, "Example Energy", "Abschlag Strom Vertrag", -8000, "MD-POWER-01", "XX00ZZZ0000001", 2),
            H(3, Gas, "Example Energy", "Abschlag Gas Vertrag", -6000, "MD-HEAT-02", "XX00ZZZ0000001", 1),
            H(4, Gas, "Example Energy", "Abschlag Gas Vertrag", -6000, "MD-HEAT-02", "XX00ZZZ0000001", 2),
            H(5, Gas, "Example Energy", "Abschlag Gas Vertrag", -6000, "MD-HEAT-02", "XX00ZZZ0000001", 3),
        ]);
        // Reads like the gas bookings, but carries the electricity mandate (and a new amount).
        var vote = m.Decide(T("Example Energy", "Abschlag Gas Vertrag", -9100, "md-power-01", "XX00ZZZ0000001"), null, 0.75, out _);

        Assert.Equal((Electricity, "mandate"), (vote!.CategoryId, vote.Source));
        Assert.True(vote.Confidence >= 0.75);
        // A restriction that rules out the mandate's category leaves the mandate out.
        Assert.Null(m.MandateVote(T("Example Energy", "x", -1, "MD-POWER-01"), c => c != Electricity));
    }

    [Fact]
    public void The_same_creditor_alone_is_a_weaker_vote()
    {
        var m = new CategoryMatching(
        [
            H(1, Insurance, "Example Insurance", "Beitrag Haftpflicht", -500, "MD-A", "XX11ZZZ0000002", 1),
            H(2, Insurance, "Example Insurance", "Beitrag Hausrat", -700, "MD-B", "XX11ZZZ0000002", 2),
            H(3, Insurance, "Example Insurance", "Beitrag Rechtsschutz", -900, "MD-C", "XX11ZZZ0000002", 3),
        ]);
        var vote = m.CreditorVote(T("Other Name", "Beitrag", -1100, "MD-NEW", "XX11ZZZ0000002"));

        Assert.Equal((Insurance, "creditor"), (vote!.CategoryId, vote.Source));
        Assert.True(vote.Confidence < 0.75);
        Assert.Null(m.MandateVote(T("Other Name", "Beitrag", -1100, "MD-NEW", "XX11ZZZ0000002")));
    }

    [Fact]
    public void A_recurring_payee_and_amount_tells_a_subscription_from_single_purchases()
    {
        var m = new CategoryMatching(
        [
            H(1, Streaming, "EXAMPLE.STORE/BILL", "", -999, month: 6),
            H(2, Streaming, "EXAMPLE.STORE/BILL", "", -999, month: 7),
            H(3, Streaming, "EXAMPLE.STORE/BILL", "", -999, month: 8),
            H(4, Apps, "EXAMPLE.STORE/BILL", "", -199, month: 6),
            H(5, Apps, "EXAMPLE.STORE/BILL", "", -499, month: 7),
            H(6, Apps, "EXAMPLE.STORE/BILL", "", -1299, month: 8),
            H(7, Apps, "EXAMPLE.STORE/BILL", "", -299, month: 9),
            H(8, Apps, "EXAMPLE.STORE/BILL", "", -999, month: -12), // too old to count as the same recurring charge
        ]);

        var vote = m.Decide(T("EXAMPLE.STORE/BILL", "", -1000), null, 0.75, out _);
        Assert.Equal((Streaming, "amount"), (vote!.CategoryId, vote.Source));
        Assert.True(vote.Confidence >= 0.75);

        // A one-off amount falls back to the neighbours, where closer amounts weigh more.
        var other = m.Decide(T("EXAMPLE.STORE/BILL", "", -249), null, 0.75, out var neighbours);
        Assert.Equal((Apps, "knn"), (other!.CategoryId, other.Source));
        Assert.True(neighbours[0].Booking.Amount is -199 or -299);
        Assert.Equal("same payee and amount 3 times in 18 months, about monthly",
            CategoryMatching.Recurrence(Start.AddMonths(9), -999, [(Start.AddMonths(6), -999), (Start.AddMonths(7), -1000), (Start.AddMonths(8), -999), (Start.AddMonths(8), -199)]));
    }

    [Fact]
    public void Mandate_and_creditor_tokens_are_stable_per_request_and_the_raw_values_never_leave()
    {
        var r = new AssistRedactor([]);
        static AssistBookingSource S(long id, string? mandate, string? creditor, string? purpose = "Abschlag") =>
            new(id, Start, -8000, "EUR", "Example Energy", purpose, MandateReference: mandate, CreditorId: creditor);

        var a = r.Redact(S(1, "MD-POWER-01", "XX00ZZZ0000001"));
        var b = r.Redact(S(2, "md-power-01 ", "XX00ZZZ0000001"));
        var c = r.Redact(S(3, "MD-HEAT-02", "XX00ZZZ0000001"));
        // Only in the purpose's SEPA tags.
        var d = r.Redact(S(4, null, null, "EREF+E2E-9 MREF+MD-HEAT-02 CRED+XX22ZZZ0000003 SVWZ+Abschlag Gas MD-HEAT-02"));

        Assert.Equal(("MANDATE_1", "CREDITOR_1"), (a.Mandate, a.Creditor));
        Assert.Equal(("MANDATE_1", "CREDITOR_1"), (b.Mandate, b.Creditor));
        Assert.Equal(("MANDATE_2", "CREDITOR_1"), (c.Mandate, c.Creditor));
        Assert.Equal(("MANDATE_2", "CREDITOR_2"), (d.Mandate, d.Creditor));
        var json = CategoryPrompt.UserContent([new PromptItem(a, [new PromptNeighbour(b, "c1", "Housing › Electricity", true, true, true)]), new PromptItem(c, []), new PromptItem(d, [])]);
        foreach (var secret in new[] { "MD-POWER", "POWER-01", "MD-HEAT", "ZZZ", "E2E-9" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"same_mandate\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_carries_account_role_contract_tokens_recurrence_order_items_and_neighbour_relations()
    {
        var accounts = new[]
        {
            new Account { Id = 10, OwnerUserId = "u", Name = "Main Giro", Type = 1 },
            new Account { Id = 11, OwnerUserId = "u", Name = "Wallet", Type = 0, Provider = "paypal" },
        };
        static Transaction Tx(long id, long account, long amount, int month, long? category = null, string? mandate = null, string? items = null) => new()
        {
            Id = id,
            OwnerUserId = "u",
            AccountId = account,
            Amount = amount,
            Name = "Example Energy",
            Purpose = "Abschlag",
            CategoryId = category,
            MandateReference = mandate,
            CreditorId = mandate == null ? null : "XX00ZZZ0000001",
            OrderItems = items,
            Timestamp = new DateTimeOffset(Start.AddMonths(month).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        var rows = new[]
        {
            Tx(1, 10, -8000, 1, Electricity, "MD-POWER-01"), Tx(2, 10, -8000, 2, Electricity, "MD-POWER-01"), Tx(3, 10, -8000, 3, Electricity, "MD-POWER-01"),
            Tx(4, 10, -6000, 3, Gas, "MD-HEAT-02"),
            Tx(9, 11, -8000, 4, null, "MD-POWER-01", "Desk lamp; Light bulbs ×2"),
        }.ToDictionary(t => t.Id);
        var rule = new CategoryRule { CategoryId = Gas, Expression = "name:\"Example Energy\" purpose:Gas", Enabled = true };
        var secret = new CategoryRule { CategoryId = Gas, Expression = "mandate:\"MD-HEAT-02\"", Enabled = true };
        var tokens = new CategoryEvidence(new Dictionary<long, Transaction>(), accounts, new Dictionary<long, PromptCategory>());
        var categories = CategoryEvidence.Categories([(Electricity, "Housing › Electricity", false), (Gas, "Housing › Gas", false)], [rule, secret], [10, 11],
            (c, a) => c != Gas || a == 10, a => tokens.AccountToken(a), []);
        var evidence = new CategoryEvidence(rows, accounts, categories.ToDictionary(c => c.Id));
        var matching = new CategoryMatching(rows.Values.Where(t => t.CategoryId != null).Select(t => CategoryEvidence.History(t, t.CategoryId!.Value)));
        var target = CategoryEvidence.Target(rows[9]);
        matching.Decide(target, null, 0.75, out var knn);

        var item = evidence.Item(new AssistRedactor([]), rows[9], CategoryEvidence.PromptNeighbours(matching, target, knn));
        var json = CategoryPrompt.UserContent([item]);
        var node = JsonNode.Parse(json["Bookings to categorize:\n".Length..])![0]!;

        Assert.Equal(("OWN_2", "PayPal wallet", "MANDATE_1", "CREDITOR_1"),
            ((string?)node["account"], (string?)node["account_role"], (string?)node["mandate"], (string?)node["creditor"]));
        Assert.Equal("same payee and amount 3 times in 18 months, about monthly", (string?)node["recurrence"]);
        Assert.Equal("Desk lamp; Light bulbs ×2", (string?)node["order_items"]);
        var first = node["similar_categorized"]![0]!;
        Assert.Equal(("c1", "Housing › Electricity", true, true, true), ((string?)first["category_id"], (string?)first["category_path"],
            (bool)first["same_mandate"]!, (bool)first["same_creditor"]!, (bool)first["same_amount"]!));
        Assert.Contains(node["similar_categorized"]!.AsArray(), n => !(bool)n!["same_mandate"]! && (string?)n["category_id"] == "c2");
        Assert.DoesNotContain("MD-", json, StringComparison.Ordinal);

        var system = CategoryPrompt.SystemPrompt(categories);
        Assert.Contains("c1 | Housing › Electricity | expense", system, StringComparison.Ordinal);
        Assert.Contains("c2 | Housing › Gas | expense | only OWN_1 | rules: name:\"Example Energy\" AND purpose:Gas", system, StringComparison.Ordinal);
        Assert.DoesNotContain("MD-HEAT", system, StringComparison.Ordinal);
        Assert.Contains("Prefer contract identity", system, StringComparison.Ordinal);
        Assert.Contains("PayPal", system, StringComparison.Ordinal);
        Assert.Contains("order_items", system, StringComparison.Ordinal);

        var decision = DecisionsCategoryChooser.BuildRequest(item, categories);
        Assert.Equal(json, decision.Input);
        Assert.Contains("Prefer contract identity", decision.Questions[0].Instructions, StringComparison.Ordinal);
        Assert.Equal("Housing › Gas | expense | only OWN_1 | rules: name:\"Example Energy\" AND purpose:Gas", decision.Questions[0].Choices[1].Description);
    }
}
