using OpenBudget.Assist;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>What leaves the server: no IBANs, references, card numbers or the household's names.</summary>
public sealed class AssistRedactorTests
{
    private static readonly string[] Household = ["Erika Mustermann", "maxi_m"];

    private static AssistBookingSource Booking(string? purpose, string? name = "Example Market GmbH", string? iban = "DE89370400440532013000") => new(
        4711, new DateOnly(2026, 3, 12), -4599, "EUR", name, purpose, "Lastschrift", "105", "Giro",
        iban, "COBADEFFXXX", "MANDATE-000123", "DE98ZZZ09999999999", "E2E-REF-2026-0042");

    [Fact]
    public void A_sepa_direct_debit_keeps_only_the_remittance_text()
    {
        var r = new AssistRedactor(Household);
        var b = r.Redact(Booking("EREF+E2E-REF-2026-0042 MREF+MANDATE-000123 CRED+DE98ZZZ09999999999 SVWZ+Einkauf Filiale 12 Erika Mustermann IBAN DE02120300000000202051"));

        Assert.Equal("t1", b.Tx);
        Assert.Equal("2026-03-12", b.Date);
        Assert.Equal(-45.99m, b.Amount);
        Assert.Equal("Example Market GmbH", b.Payee);
        Assert.Equal("Einkauf Filiale 12 PERSON_A", b.Purpose);
        Assert.Equal("Lastschrift GVC 105", b.Code);
        Assert.Equal("Giro", b.AccountType);
        Assert.Equal("ACC_1", b.Counterparty);
    }

    [Theory]
    [InlineData("Miete Wohnung 3 OG DE02 1203 0000 0000 2020 51", "Miete Wohnung 3 OG ACC_2")]
    [InlineData("Kartenzahlung 4111 1111 1111 1111 Tankstelle", "Kartenzahlung [CARD] Tankstelle")]
    [InlineData("VISA XXXX XXXX XXXX 1234 Hotel", "VISA [CARD] Hotel")]
    [InlineData("Rechnung 2026123456 Kd-Nr 98765432", "Rechnung # Kd-Nr #")]
    [InlineData("Mandatsreferenz: ABC-778899 Beitrag", "Beitrag")]
    [InlineData("Gläubiger-ID DE98ZZZ09999999999 Beitrag", "Beitrag")]
    [InlineData("Taschengeld von MUSTERMANN, ERIKA", "Taschengeld von PERSON_A")]
    [InlineData("Erstattung an maxi_m@example.org", "Erstattung an [EMAIL]")]
    public void Identifiers_and_names_are_removed(string purpose, string expected)
    {
        var r = new AssistRedactor(Household);
        Assert.Equal(expected, r.Redact(Booking(purpose)).Purpose);
    }

    [Fact]
    public void The_users_own_names_are_tokens_also_as_payee()
    {
        var r = new AssistRedactor(Household);
        Assert.Equal("PERSON_A", r.Redact(Booking("Umbuchung", name: "Erika Mustermann")).Payee);
        Assert.Equal("PERSON_B", r.Redact(Booking("Umbuchung", name: "Maxi")).Payee);
    }

    [Fact]
    public void Pseudonyms_and_account_tokens_are_stable_within_a_request_and_resolve_back()
    {
        var r = new AssistRedactor([]);
        var first = r.Redact(Booking("A"));
        var again = r.Redact(Booking("B"));
        var other = r.Redact(Booking("C", iban: "DE02 1203 0000 0000 2020 51") with { Id = 5 });

        Assert.Equal(first.Tx, again.Tx);
        Assert.Equal("ACC_1", again.Counterparty);
        Assert.Equal(("t2", "ACC_2"), (other.Tx, other.Counterparty));
        Assert.Equal(4711, r.Resolve("t1"));
        Assert.Null(r.Resolve("t99"));
    }

    [Fact]
    public void Golden_serialized_batch_contains_nothing_identifying()
    {
        var r = new AssistRedactor(Household);
        var source = Booking("EREF+E2E-REF-2026-0042 MREF+MANDATE-000123 CRED+DE98ZZZ09999999999 SVWZ+Beitrag 2026 Erika Mustermann");
        var json = CategoryPrompt.UserContent([new PromptItem(r.Redact(source), [(r.Redact(source with { Id = 1 }), "c4")])]);

        foreach (var secret in new[] { "DE89", "370400440532013000", "COBADEFF", "MANDATE", "ZZZ", "E2E-REF", "Erika", "Mustermann", "4711" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            """Bookings to categorize:""" + "\n" +
            """[{"tx":"t1","date":"2026-03-12","amount":-45.99,"currency":"EUR","payee":"Example Market GmbH","purpose":"Beitrag 2026 PERSON_A","code":"Lastschrift GVC 105","account_type":"Giro","counterparty":"ACC_1","similar_categorized":[{"date":"2026-03-12","amount":-45.99,"currency":"EUR","payee":"Example Market GmbH","purpose":"Beitrag 2026 PERSON_A","code":"Lastschrift GVC 105","account_type":"Giro","counterparty":"ACC_1","category_id":"c4"}]}]""",
            json);
    }
}
