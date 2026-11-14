using System.Globalization;
using System.Text.RegularExpressions;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// REWE in-store receipts sent as eBon mails ("Dein REWE eBon vom 31.10.2022" with an attachment "REWE-eBon.pdf"). The
/// PDF is the till receipt: item lines "NAME amount VAT [*]" between the "EUR" heading and "SUMME"; a line
/// "N Stk x price" or "0,812 kg x 1,99 EUR/kg" gives the quantity of the item above it, and a negative line (a
/// discount or coupon) is taken off the item above it (returned empties stay lines of their own); a count may carry
/// decimals ("0.521 Stk x 9,98"). The lines must add up to "SUMME". Payments read "Geg. &lt;means&gt; EUR amount": what
/// was paid by card or app (girocard, VISA, PAYBACK PAY, …) is the payment to match; cash ("BAR") is not, so a
/// cash-only receipt is stored but never matched. The receipt's identity is its "ReceiptID", else market, till,
/// receipt number and day (the day from "Datum:", else from the "dd.MM.yyyy HH:mm Bon-Nr.:…" line). The address lines
/// under the "R E W E" heading (up to the tax number or the "EUR" heading) are the store.
/// </summary>
public static partial class ReweEbon
{
    /// <summary>The search phrase for the mailbox (the subject's fixed part).</summary>
    public const string SubjectPrefix = "Dein REWE eBon";

    [GeneratedRegex(@"^Dein REWE eBon vom \d\d\.\d\d\.\d{4}\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SubjectPattern();

    [GeneratedRegex(@"^(?<name>.+?) (?<amount>-?\d+(?:\.\d{3})*,\d\d) (?<vat>[AB])(?<star> \*)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^(?<n>\d+(?:[.,]\d+)?) Stk x (?<price>-?[\d.,]+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex CountLine();

    [GeneratedRegex(@"^(?<kg>\d+,\d+) kg x (?<price>[\d.,]+) EUR/kg$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex WeightLine();

    [GeneratedRegex(@"^SUMME EUR (?<v>-?[\d.,]+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Sum();

    [GeneratedRegex(@"^Geg\. (?<means>.+?) EUR (?<v>-?[\d.,]+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Given();

    [GeneratedRegex(@"^Datum: (?<d>\d\d\.\d\d\.\d{4})$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"^(?<d>\d\d\.\d\d\.\d{4}) \d\d:\d\d\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex StampLine();

    [GeneratedRegex(@"^ReceiptID: ?(?<id>[0-9A-Za-z]+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ReceiptId();

    [GeneratedRegex(@"Markt: ?(?<m>\d+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Market();

    [GeneratedRegex(@"Kasse: ?(?<k>\d+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Till();

    [GeneratedRegex(@"Bon-Nr\.: ?(?<b>\d+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex BonNumber();

    public static bool IsEbonSubject(string? subject) => subject != null && SubjectPattern().IsMatch(subject.Trim());

    public static bool IsEbonAttachment(string? name) => string.Equals(name?.Trim(), "REWE-eBon.pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the PDF; throws <see cref="InvalidDataException"/> when it is not a REWE eBon whose lines add up.</summary>
    public static ParsedReceipt Parse(byte[] pdf) => Parse(ReceiptPdf.Lines(pdf));

    /// <summary>Parses the receipt's text lines (see the class remarks); throws <see cref="InvalidDataException"/>.</summary>
    public static ParsedReceipt Parse(IReadOnlyList<string> rawLines)
    {
        var items = new List<ParsedReceiptItem>();
        long? sum = null;
        long card = 0;
        string? id = null, market = null, till = null, bon = null;
        DateOnly? date = null;
        var isRewe = false;
        var inItems = false;
        var inAddress = false;
        var address = new List<string>();
        foreach (var raw in rawLines)
        {
            var line = ReceiptPdf.Normalize(raw);
            // Item names may start with "REWE" too ("REWE PIZZA"): within the items it is no heading.
            if (line is "R E W E" || (!inItems && line.StartsWith("REWE", StringComparison.Ordinal)))
            {
                isRewe = true;
                inAddress = line == "R E W E" && address.Count == 0;
                continue;
            }
            if (inAddress)
            {
                if (line == "EUR" || line.StartsWith("UID", StringComparison.Ordinal) || address.Count >= 3) inAddress = false;
                else address.Add(line);
            }
            if (sum == null && line == "EUR")
            {
                inItems = true;
                continue;
            }
            if (Sum().Match(line) is { Success: true } s)
            {
                sum ??= ReweInvoice.Cents(s.Groups["v"].Value);
                inItems = false;
                continue;
            }
            if (inItems)
            {
                if (CountLine().Match(line) is { Success: true } c && items.Count > 0)
                {
                    items[^1] = items[^1] with { Quantity = c.Groups["n"].Value, UnitCents = ReweInvoice.Cents(c.Groups["price"].Value) };
                }
                else if (WeightLine().Match(line) is { Success: true } w && items.Count > 0)
                {
                    var grams = (long)Math.Round(decimal.Parse(w.Groups["kg"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) * 1000m);
                    items[^1] = items[^1] with { Quantity = grams.ToString(CultureInfo.InvariantCulture) + "g", UnitCents = ReweInvoice.Cents(w.Groups["price"].Value) };
                }
                else if (ItemLine().Match(line) is { Success: true } m)
                {
                    var name = m.Groups["name"].Value.Trim();
                    var cents = ReweInvoice.Cents(m.Groups["amount"].Value) ?? throw new InvalidDataException("An item line has no readable amount.");
                    var kind = ReweInvoice.KindOf(name);
                    if (cents < 0 && kind == ReceiptItemKind.Product && items.Count > 0)
                        items[^1] = items[^1] with { TotalCents = items[^1].TotalCents + cents };
                    else
                        items.Add(new ParsedReceiptItem(name, "1", m.Groups["vat"].Value, null, cents, m.Groups["star"].Success, kind));
                }
                continue;
            }
            if (Given().Match(line) is { Success: true } g)
            {
                if (!g.Groups["means"].Value.Contains("BAR", StringComparison.OrdinalIgnoreCase)) card += ReweInvoice.Cents(g.Groups["v"].Value) ?? 0;
                continue;
            }
            if (DateLine().Match(line) is { Success: true } d)
                date ??= DateOnly.ParseExact(d.Groups["d"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture);
            if (StampLine().Match(line) is { Success: true } st
                && DateOnly.TryParseExact(st.Groups["d"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamped))
                date ??= stamped;
            if (ReceiptId().Match(line) is { Success: true } r) id ??= r.Groups["id"].Value;
            if (Market().Match(line) is { Success: true } mk) market ??= mk.Groups["m"].Value;
            if (Till().Match(line) is { Success: true } k) till ??= k.Groups["k"].Value;
            if (BonNumber().Match(line) is { Success: true } b) bon ??= b.Groups["b"].Value;
        }

        if (!isRewe || date == null) throw new InvalidDataException("Not a REWE eBon.");
        if (items.Count == 0 || sum == null) throw new InvalidDataException("The eBon lists no items or no sum.");
        if (items.Sum(i => i.TotalCents) != sum) throw new InvalidDataException("The eBon's lines do not add up to its sum.");
        id ??= market != null && bon != null
            ? $"M{market}-K{till ?? "0"}-B{bon}-{date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}"
            : throw new InvalidDataException("The eBon has no receipt number.");
        var label = market != null && bon != null ? $"Markt {market} Bon {bon}" : null;
        return new ParsedReceipt(id.Length <= 40 ? id : id[^40..], label, date, date, date, items, sum.Value, 0, card)
        {
            Store = address.Count > 0 ? "REWE " + string.Join(", ", address) : null,
        };
    }
}
