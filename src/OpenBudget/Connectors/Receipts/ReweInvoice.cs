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
/// price); "*" after a name marks a line without discount; deposit lines and the delivery fee are lines like the
/// others. The totals: "Gesamtsumme" (the sum of the lines, checked), an optional redeemed bonus credit and the
/// "Zahlungsbetrag" that is charged. Any difference between the payment and the sum counts as the bonus.
/// </summary>
public static partial class ReweInvoice
{
    [GeneratedRegex(@"^Deine Rechnung zu B-[A-Z0-9]{3}-[A-Z0-9]{3}-[A-Z0-9]{3}\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SubjectPattern();

    [GeneratedRegex(@"^Rechnung_PN\d+\.pdf$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AttachmentPattern();

    [GeneratedRegex(@"^(?<name>.+?) (?<qty>-?\d+(?:g)?) (?<vat>A/B|A|B) (?<unit>-?[\d.,]+) €(?:/kg)? (?<total>-?[\d.,]+) €$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"(?:^|\s)(?<label>Belegnummer|Bestellnummer|Bestelldatum|Liefertermin|Rechnungsdatum) (?<value>\S+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Field();

    [GeneratedRegex(@"^(?<label>Gesamtsumme|Eingelöstes Bonus-Guthaben|Zahlungsbetrag) (?<value>-?[\d.,]+) €$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
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
        long bonus = 0;
        var items = new List<ParsedReceiptItem>();
        var inItems = false;
        var isRewe = false;
        foreach (var raw in rawLines)
        {
            var line = ReceiptPdf.Normalize(raw);
            if (line.StartsWith("REWE", StringComparison.Ordinal)) isRewe = true;
            // The table header; the PDF's text may put "Produkt" on a line of its own.
            if (line.Contains("Menge MwSt. Einzelpreis Gesamt", StringComparison.Ordinal))
            {
                inItems = true;
                continue;
            }
            if (Total().Match(line) is { Success: true } t)
            {
                var cents = Cents(t.Groups["value"].Value);
                switch (t.Groups["label"].Value)
                {
                    case "Gesamtsumme":
                        total ??= cents;
                        inItems = false;
                        break;
                    case "Zahlungsbetrag":
                        payment ??= cents;
                        break;
                    default:
                        bonus += cents ?? 0;
                        break;
                }
                continue;
            }
            if (inItems && ItemLine().Match(line) is { Success: true } m)
            {
                var name = m.Groups["name"].Value.Trim();
                var noDiscount = name.EndsWith('*', StringComparison.Ordinal);
                if (noDiscount) name = name.TrimEnd('*').TrimEnd();
                var lineTotal = Cents(m.Groups["total"].Value) ?? throw new InvalidDataException("An item line has no readable total.");
                items.Add(new ParsedReceiptItem(name, m.Groups["qty"].Value, m.Groups["vat"].Value, Cents(m.Groups["unit"].Value), lineTotal, noDiscount, KindOf(name)));
                continue;
            }
            if (!inItems && Field().Match(line) is { Success: true } f)
            {
                var value = f.Groups["value"].Value;
                switch (f.Groups["label"].Value)
                {
                    case "Belegnummer": invoice ??= value; break;
                    case "Bestellnummer": order ??= value; break;
                    case "Bestelldatum": ordered ??= Date(value); break;
                    case "Liefertermin": delivered ??= Date(value); break;
                    case "Rechnungsdatum": invoiced ??= Date(value); break;
                }
            }
        }

        if (!isRewe || invoice == null) throw new InvalidDataException("Not a REWE invoice.");
        if (items.Count == 0 || total == null) throw new InvalidDataException("The invoice lists no items or no total.");
        if (items.Sum(i => i.TotalCents) != total) throw new InvalidDataException("The invoice's lines do not add up to its total.");
        var paid = payment ?? total.Value + bonus;
        return new ParsedReceipt(invoice, order, ordered, delivered, invoiced, items, total.Value, paid - total.Value, paid);
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
