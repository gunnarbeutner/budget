using System.Globalization;
using System.Text.RegularExpressions;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>The fields of a booking the redactor looks at.</summary>
public sealed record AssistBookingSource(
    long Id, DateOnly Date, long AmountCents, string? Currency, string? Name, string? Purpose,
    string? BookingText = null, string? TransactionCode = null, string? AccountKind = null,
    string? CounterpartyIban = null, string? CounterpartyBic = null,
    string? MandateReference = null, string? CreditorId = null, string? EndToEndReference = null, string? BankReference = null,
    string? OrderItems = null, string? AccountRole = null, string? OwnAccount = null, string? Recurrence = null);

/// <summary>
/// What is sent for one booking: no account names, balances, IBANs or references. <see cref="Account"/> is a stable
/// token of the household's own account ("OWN_2"), <see cref="Mandate"/> and <see cref="Creditor"/> are per-request
/// tokens of the SEPA mandate reference and creditor id ("MANDATE_1"), <see cref="Recurrence"/> says how often the
/// payee and amount recur.
/// </summary>
public sealed record AssistBooking(
    string Tx, string Date, decimal Amount, string Currency, string Payee, string Purpose, string Code, string AccountType, string? Counterparty,
    string? Account = null, string? AccountRole = null, string? Mandate = null, string? Creditor = null, string? Recurrence = null, string? OrderItems = null);

/// <summary>
/// Turns bookings into what may leave the server. Keeps the cleaned purpose text, payee, signed amount, date, booking
/// code, account type and role, order items; ids become pseudonyms ("t1", "t2", …), a counterparty IBAN a stable token
/// ("ACC_1"), a mandate reference "MANDATE_1" and a creditor id "CREDITOR_1", so bookings of the same counterparty or
/// contract stay recognisable. IBANs, BICs, mandate and creditor ids, end-to-end references,
/// card numbers, long digit runs and the household's own names (as PERSON_A, PERSON_B, …) are removed. One instance
/// covers one request: its pseudonyms are only meaningful inside it.
/// </summary>
public sealed partial class AssistRedactor
{
    private const int MaxPurpose = 160;
    private readonly List<(Regex Pattern, string Token)> _names = [];
    private readonly Dictionary<string, string> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mandates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _creditors = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _ids = new();
    private readonly Dictionary<string, long> _back = new(StringComparer.Ordinal);

    /// <summary>Creates a redactor for one request.</summary>
    /// <param name="personNames">The users' own names (display and user names); each person gets one token.</param>
    public AssistRedactor(IEnumerable<string?> personNames)
    {
        var index = 0;
        foreach (var raw in personNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var token = "PERSON_" + (char)('A' + Math.Min(index++, 25));
            // The full name first, then each part of it (and "Last, First" orders come out right as well).
            _names.Add((NameRegex(raw), token));
            foreach (var part in raw.Split([' ', ',', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 3))
                _names.Add((NameRegex(part), token));
        }
    }

    private static Regex NameRegex(string name) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(name).Replace(@"\ ", @"[\s,]+", StringComparison.Ordinal) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>The pseudonym of a booking id in this request.</summary>
    public string IdFor(long id)
    {
        if (_ids.TryGetValue(id, out var t)) return t;
        t = "t" + (_ids.Count + 1).ToString(CultureInfo.InvariantCulture);
        _ids[id] = t;
        _back[t] = id;
        return t;
    }

    /// <summary>The booking id behind a pseudonym of this request, or null for an unknown one.</summary>
    public long? Resolve(string? pseudonym) => pseudonym != null && _back.TryGetValue(pseudonym, out var id) ? id : null;

    public string AccountToken(string iban)
    {
        var key = Compact(iban);
        if (_accounts.TryGetValue(key, out var token)) return token;
        token = "ACC_" + (_accounts.Count + 1).ToString(CultureInfo.InvariantCulture);
        _accounts[key] = token;
        return token;
    }

    /// <summary>The token of a mandate reference in this request ("MANDATE_1"); the same reference gets the same token.</summary>
    public string MandateToken(string mandate) => Token(_mandates, "MANDATE_", mandate);

    /// <summary>The token of a creditor id in this request ("CREDITOR_1").</summary>
    public string CreditorToken(string creditorId) => Token(_creditors, "CREDITOR_", creditorId);

    private static string Token(Dictionary<string, string> map, string prefix, string value)
    {
        var key = Compact(value);
        if (map.TryGetValue(key, out var token)) return token;
        token = prefix + (map.Count + 1).ToString(CultureInfo.InvariantCulture);
        map[key] = token;
        return token;
    }

    public AssistBooking Redact(AssistBookingSource s)
    {
        var tags = SepaPurpose.Parse(s.Purpose);
        var mandate = CategoryMatching.SepaKey(s.MandateReference) ?? CategoryMatching.SepaKey(tags.GetValueOrDefault("MREF"));
        var creditor = CategoryMatching.SepaKey(s.CreditorId) ?? CategoryMatching.SepaKey(tags.GetValueOrDefault("CRED"));
        var secrets = new[] { s.MandateReference, s.CreditorId, s.EndToEndReference, s.BankReference, s.CounterpartyBic, s.CounterpartyIban, mandate, creditor }
            .Where(v => !string.IsNullOrWhiteSpace(v) && v!.Trim().Length >= 4).Select(v => v!.Trim()).ToList();
        // The counterparty gets its token before any IBAN in the texts does.
        var counterparty = string.IsNullOrWhiteSpace(s.CounterpartyIban) ? null : AccountToken(s.CounterpartyIban!);
        return new AssistBooking(
            IdFor(s.Id),
            s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s.AmountCents / 100m,
            string.IsNullOrWhiteSpace(s.Currency) ? "EUR" : s.Currency!,
            CleanText(s.Name, secrets, 80),
            CleanPurpose(s.Purpose, secrets),
            CleanText(string.Join(" ", new[] { s.BookingText, s.TransactionCode is { Length: > 0 } c ? "GVC " + c : null }.Where(x => !string.IsNullOrWhiteSpace(x))), secrets, 60),
            s.AccountKind ?? "",
            counterparty,
            s.OwnAccount,
            s.AccountRole,
            mandate == null ? null : MandateToken(mandate),
            creditor == null ? null : CreditorToken(creditor),
            s.Recurrence,
            string.IsNullOrWhiteSpace(s.OrderItems) ? null : CleanText(s.OrderItems, secrets, 240));
    }

    /// <summary>The purpose without SEPA reference tags: the remittance text (SVWZ) when tagged, otherwise all text.</summary>
    public string CleanPurpose(string? purpose, IReadOnlyCollection<string>? secrets = null)
    {
        if (string.IsNullOrWhiteSpace(purpose)) return "";
        var tags = SepaPurpose.Parse(purpose);
        var text = tags.TryGetValue("SVWZ", out var svwz) ? svwz
            : tags.Count > 0 ? SepaTag().Replace(purpose, " ")
            : purpose;
        if (tags.Count > 0 && !tags.ContainsKey("SVWZ"))
            foreach (var v in tags.Values) text = text.Replace(v, " ", StringComparison.Ordinal);
        return CleanText(text, secrets ?? [], MaxPurpose);
    }

    public string CleanText(string? text, IReadOnlyCollection<string> secrets, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Labels first, so that a label whose value is a known secret goes along with it.
        var t = LabelledId().Replace(text, " ");
        foreach (var secret in secrets)
            t = t.Replace(secret, " ", StringComparison.OrdinalIgnoreCase);
        t = Iban().Replace(t, m => " " + AccountToken(m.Value) + " ");
        t = CreditorIdPattern().Replace(t, " ");
        t = Card().Replace(t, " [CARD] ");
        t = MaskedCard().Replace(t, " [CARD] ");
        t = Email().Replace(t, " [EMAIL] ");
        t = LongDigits().Replace(t, "#");
        foreach (var (pattern, token) in _names)
            t = pattern.Replace(t, token);
        t = RepeatedPerson().Replace(t, "${p}");
        t = Spaces().Replace(t, " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    private static string Compact(string iban) => Spaces().Replace(iban, "").ToUpperInvariant();

    [GeneratedRegex(@"(?:EREF|KREF|MREF|CRED|DEBT|SVWZ|ABWA|ABWE|IBAN|BIC|COAM|OAMT|ORDP|BENM|PURP|RTRN)\+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SepaTag();

    /// <summary>"IBAN: …", "BIC …", "Mandat …", "Gläubiger-ID …", "End-to-End-Ref. …" and the value after it.</summary>
    [GeneratedRegex(@"\b(?:IBAN|BIC|Mandat(?:sreferenz|sref\.?|-?Ref\.?)?|MREF|Gl[aä]ubiger-?ID|CRED|End-?to-?End-?Ref(?:erenz|\.)?|EREF|KREF|Kundenreferenz)\s*[:.]?\s*\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LabelledId();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){2,7}(?:\s?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}ZZZ[A-Z0-9]{3,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreditorIdPattern();

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Card();

    [GeneratedRegex(@"\b\d{0,6}[X*]{4,}[X*\d ]*\d{2,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MaskedCard();

    [GeneratedRegex(@"\S+@\S+\.\w+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();

    /// <summary>"PERSON_A, PERSON_A" (first and last name replaced separately) → "PERSON_A".</summary>
    [GeneratedRegex(@"(?<p>PERSON_[A-Z])(?:[\s,]+\k<p>)+", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepeatedPerson();
}
