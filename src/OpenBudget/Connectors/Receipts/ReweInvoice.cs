using System.Globalization;
using System.Text.RegularExpressions;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>A line of a parsed invoice; amounts in cents.</summary>
public sealed record ParsedReceiptItem(
    string Name, string Quantity, string? VatCode, long? UnitCents, long TotalCents, bool NoDiscount, ReceiptItemKind Kind);

/// <summary>A parsed invoice; amounts in cents, <see cref="BonusCents"/> zero or negative.</summary>
public sealed record ParsedReceipt(
    string InvoiceNumber, string? OrderNumber, DateOnly? OrderDate, DateOnly? DeliveryDate, DateOnly? InvoiceDate,
    IReadOnlyList<ParsedReceiptItem> Items, long TotalCents, long BonusCents, long PaymentCents)
{
    /// <summary>The store's name and address, when the receipt shows them ("REWE Beispielweg 9, 12345 Musterstadt").</summary>
    public string? Store { get; init; }
}

/// <summary>
/// REWE online-order invoices: which mails carry one ("Deine Rechnung zu B-…" with an attachment "Rechnung_PN….pdf"),
/// and the invoice's text. Item lines read "Name Quantity VAT Unit € Total €" (a weight quantity has a per-kg unit
/// price); "*" after a name (or after the total) marks a line without discount; deposit lines and the delivery fee are
/// lines like the others. The totals: "Gesamtsumme" (the sum of the lines, checked), an optional redeemed bonus credit
/// and the "Zahlungsbetrag" that is charged. Any difference between the payment and the sum counts as the bonus.
///
/// The older layout reads "Rechnung PN…" with "Label: value" fields and "Lieferdatum", the table header
/// "Bezeichnung Menge MwSt. Einzelpreis Summe Pos.", and "Summe" for the total. A long name wraps around its values:
/// one line of the name, then "Quantity VAT Unit € Total €" alone, then the rest of the name. "Rabatt A -0,50 €" under
/// an item is a discount on it; "Summe Rabatt Gesamtpositionen …" is a discount on the whole order (taken as bonus).
/// Page footers and repeated table headers are skipped.
/// </summary>
public static partial class ReweInvoice
{
    [GeneratedRegex(@"^Deine Rechnung zu B-[A-Z0-9]{3}-[A-Z0-9]{3}-[A-Z0-9]{3}\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SubjectPattern();

    [GeneratedRegex(@"^Rechnung_PN\d+\.pdf$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AttachmentPattern();

    [GeneratedRegex(@"^(?<name>.+?) (?<qty>-?\d+(?:g)?) (?<vat>A/B|A|B) (?<unit>-?[\d.,]+) €(?:/kg)? (?<total>-?[\d.,]+) €(?<star> \*)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^(?<qty>-?\d+(?:g)?) (?<vat>A/B|A|B) (?<unit>-?[\d.,]+) €(?:/kg)? (?<total>-?[\d.,]+) €(?<star> \*)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ValuesLine();

    [GeneratedRegex(@"^Rabatt (?:A/B|A|B) (?<value>-[\d.,]+) €$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex DiscountLine();

    [GeneratedRegex(@"^Summe Rabatt Gesamtpositionen .*? (?<value>-[\d.,]+) €(?: \*+)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex OrderDiscountLine();

    [GeneratedRegex(@"(?:^|\s)(?<label>Belegnummer|Bestellnummer|Bestelldatum|Liefertermin|Lieferdatum|Rechnungsdatum):? (?<value>\S+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Field();

    [GeneratedRegex(@"^Rechnung (?<value>PN\d+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex InvoiceHeading();

    [GeneratedRegex(@"^(?<label>Gesamtsumme|Summe|Eingelöstes Bonus-Guthaben|Zahlungsbetrag|Lastschrift) (?<value>-?[\d.,]+) €$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Total();

    /// <summary>The search phrase for the mailbox (the subject's fixed part).</summary>
    public const string SubjectPrefix = "Deine Rechnung zu";

    public static bool IsInvoiceSubject(string? subject) => subject != null && SubjectPattern().IsMatch(subject.Trim());

    public static bool IsInvoiceAttachment(string? name) => name != null && AttachmentPattern().IsMatch(name.Trim());

    /// <summary>Parses the PDF; throws <see cref="InvalidDataException"/> when it is not a REWE invoice whose lines add up.</summary>
    public static ParsedReceipt Parse(byte[] pdf) => Parse(ReceiptPdf.Lines(pdf));

    /// <summary>Parses the invoice's text lines (see the class remarks); throws <see cref="InvalidDataException"/>.</summary>
    public static ParsedReceipt Parse(IReadOnlyList<string> rawLines)
    {
        string? invoice = null, order = null;
        DateOnly? ordered = null, delivered = null, invoiced = null;
        long? total = null, payment = null;
        long bonus = 0, orderDiscount = 0;
        var items = new List<ParsedReceiptItem>();
        var inItems = false;
        var inFooter = false;
        var isRewe = false;
        // the lines of names around a wrapped item's values, and the wrapped item still taking the rest of its name
        var pending = new List<string>();
        int? wrapped = null;

        void Flush()
        {
            if (wrapped is { } w && pending.Count > 0) items[w] = items[w] with { Name = (items[w].Name + " " + string.Join(' ', pending)).Trim() };
            pending.Clear();
            wrapped = null;
        }

        void Add(string name, Match m)
        {
            var noDiscount = name.EndsWith('*', StringComparison.Ordinal) || m.Groups["star"].Success;
            name = name.TrimEnd('*').TrimEnd();
            var lineTotal = Cents(m.Groups["total"].Value) ?? throw new InvalidDataException("An item line has no readable total.");
            items.Add(new ParsedReceiptItem(name, m.Groups["qty"].Value, m.Groups["vat"].Value, Cents(m.Groups["unit"].Value), lineTotal, noDiscount, KindOf(name)));
        }

        foreach (var raw in rawLines)
        {
            var line = ReceiptPdf.Normalize(raw);
            if (line.StartsWith("REWE", StringComparison.Ordinal)) isRewe = true;
            // The table header (repeated on every page); the PDF's text may put "Produkt" on a line of its own.
            if (line.Contains("Menge MwSt. Einzelpreis Gesamt", StringComparison.Ordinal)
                || line.StartsWith("Bezeichnung Menge MwSt. Einzelpreis Summe", StringComparison.Ordinal))
            {
                inItems = true;
                inFooter = false;
                continue;
            }
            if (inItems && line.Contains("Registergericht", StringComparison.Ordinal))
            {
                Flush();
                inFooter = true;
                continue;
            }
            if (inFooter) continue;
            if (inItems && OrderDiscountLine().Match(line) is { Success: true } od)
            {
                Flush();
                orderDiscount += Cents(od.Groups["value"].Value) ?? 0;
                continue;
            }
            if (Total().Match(line) is { Success: true } t)
            {
                var cents = Cents(t.Groups["value"].Value);
                switch (t.Groups["label"].Value)
                {
                    case "Gesamtsumme" or "Summe":
                        if (!inItems && total != null) break;
                        Flush();
                        total ??= cents;
                        inItems = false;
                        break;
                    case "Zahlungsbetrag" or "Lastschrift":
                        payment ??= cents;
                        break;
                    default:
                        bonus -= Math.Abs(cents ?? 0);
                        break;
                }
                continue;
            }
            if (!inItems)
            {
                if (InvoiceHeading().Match(line) is { Success: true } h) invoice ??= h.Groups["value"].Value;
                else if (Field().Match(line) is { Success: true } f)
                {
                    var value = f.Groups["value"].Value;
                    switch (f.Groups["label"].Value)
                    {
                        case "Belegnummer": invoice ??= value; break;
                        case "Bestellnummer": order ??= value; break;
                        case "Bestelldatum": ordered ??= Date(value); break;
                        case "Liefertermin" or "Lieferdatum": delivered ??= Date(value); break;
                        case "Rechnungsdatum": invoiced ??= Date(value); break;
                    }
                    if (Field().Matches(line).Skip(1).FirstOrDefault() is { } second && second.Groups["label"].Value is "Lieferdatum" or "Liefertermin")
                        delivered ??= Date(second.Groups["value"].Value);
                }
                continue;
            }
            if (DiscountLine().Match(line) is { Success: true } d && items.Count > 0)
            {
                Flush();
                items[^1] = items[^1] with { TotalCents = items[^1].TotalCents + (Cents(d.Groups["value"].Value) ?? 0) };
                continue;
            }
            if (ValuesLine().Match(line) is { Success: true } v)
            {
                // Of the lines since the last item, the last one starts this item's name; the others end the previous one's.
                var start = pending.Count > 0 ? pending[^1] : "";
                if (pending.Count > 0) pending.RemoveAt(pending.Count - 1);
                if (wrapped == null && pending.Count > 0) start = string.Join(' ', pending) + " " + start;
                Flush();
                Add(start, v);
                wrapped = items.Count - 1;
                continue;
            }
            if (ItemLine().Match(line) is { Success: true } m)
            {
                Flush();
                Add(m.Groups["name"].Value.Trim(), m);
                continue;
            }
            pending.Add(line);
        }
        Flush();

        if (!isRewe || invoice == null) throw new InvalidDataException("Not a REWE invoice.");
        if (items.Count == 0 || total == null) throw new InvalidDataException("The invoice lists no items or no total.");
        // An order-wide discount is part of the printed total and is taken as bonus, like redeemed credit.
        var sum = items.Sum(i => i.TotalCents);
        if (sum + orderDiscount != total) throw new InvalidDataException("The invoice's lines do not add up to its total.");
        var paid = payment ?? total.Value + bonus;
        return new ParsedReceipt(invoice, order, ordered, delivered, invoiced, items, sum, paid - sum, paid);
    }

    /// <summary>Deposit lines (Pfand, Leergut, Pfandtasche) and the delivery fee; everything else is a product.</summary>
    public static ReceiptItemKind KindOf(string name)
    {
        if (name.StartsWith("PFAND", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Leergut", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Pfandtasche", StringComparison.OrdinalIgnoreCase))
            return ReceiptItemKind.Deposit;
        if (name.StartsWith("Liefergebühr", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Servicegebühr", StringComparison.OrdinalIgnoreCase))
            return ReceiptItemKind.Fee;
        return ReceiptItemKind.Product;
    }

    /// <summary>"1.234,56" / "-0,90" in cents; null when it is no amount.</summary>
    public static long? Cents(string text)
    {
        var s = text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero)
            : null;
    }

    private static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
