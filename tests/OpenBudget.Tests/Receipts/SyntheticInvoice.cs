using System.Globalization;
using System.Text;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// Builds made-up REWE-style invoice PDFs: header, an item table whose columns sit apart like the real layout (amounts
/// with a no-break space before "€"), a page break with the repeated table header, the totals and a footer. Products,
/// customer and addresses are invented.
/// </summary>
internal static class SyntheticInvoice
{
    public sealed record Line(string Name, string Quantity, string Vat, string Unit, string Total);

    private const string Nbsp = "\u00A0";

    public static Line Item(string name, string quantity, string vat, decimal unit, decimal total, bool perKg = false) =>
        new(name, quantity, vat, Money(unit) + (perKg ? "/kg" : ""), Money(total));

    public static string Money(decimal amount) => amount.ToString("#,##0.00", CultureInfo.GetCultureInfo("de-DE")) + Nbsp + "€";

    /// <summary>The usual test invoice: products, a weighed item, deposit lines and the delivery fee over two pages.</summary>
    public static IReadOnlyList<Line> StandardLines() =>
    [
        Item("Beispiel Gouda gerieben 150g", "1", "B", 1.89m, 1.89m),
        Item("ja! Kochschinken 200g", "2", "B", 2.65m, 5.30m),
        Item("Musterdeo Roll-On Fresh 50ml", "1", "A", 2.95m, 2.95m),
        Item("Beispiel Banane ca. 200g", "958g", "B", 1.99m, 1.91m, perKg: true),
        Item("Apfelschorle 0,5l", "2", "A", 1.09m, 2.18m),
        Item("Spülmittel Zitrone 450ml", "1", "A", 1.45m, 1.45m),
        Item("PFAND 0,25*", "2", "A", 0.25m, 0.50m),
        Item("Leergut Glas*", "-6", "B", 0.15m, -0.90m),
        Item("Liefergebühr", "1", "A/B", 0m, 0m),
    ];

    /// <summary>A whole invoice; <paramref name="bonus"/> (negative) adds the bonus line, the payment is the total plus it.</summary>
    public static byte[] Pdf(IReadOnlyList<Line> lines, decimal bonus = 0m, string invoice = "PN10000000000001", string order = "B-AAA-BBB-CC1",
        string orderDate = "02.11.2026", string delivery = "03.11.2026", decimal? totalOverride = null)
    {
        var total = totalOverride ?? lines.Sum(l => decimal.Parse(l.Total.Replace(Nbsp + "€", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE")));
        var header = new List<string[]>
        {
            new[] { "Rechnung" },
            new[] { "REWE Markt GmbH, Beispielstr. 1, 50000 Köln" },
            new[] { "Erika Beispiel, Musterweg 7, 12345 Musterstadt" },
            new[] { "Belegnummer " + invoice },
            new[] { "Kundennummer 9999999" },
            new[] { "Bestellnummer " + order },
            new[] { "Bestelldatum " + orderDate },
            new[] { "Liefertermin " + delivery },
            new[] { "Rechnungsdatum " + delivery },
        };
        var tableHead = new[] { "Produkt", "Menge", "MwSt.", "Einzelpreis", "Gesamt" };
        var footer = new[] { "REWE Markt GmbH · Beispielbank · IBAN DE00 0000 0000 0000 0000 00" };
        var split = Math.Min(5, lines.Count);
        var page1 = header.Append(tableHead).Concat(lines.Take(split).Select(Cells)).Append(footer).Append(new[] { "Seite 1 von 2" }).ToList();
        var totals = new List<string[]> { new[] { "Gesamtsumme " + Money(total) }, new[] { "Steuersatz Nettobetrag MwSt. Betrag Gesamt" }, new[] { "A = 19 % 1,00 € 0,19 € 1,19 €" } };
        if (bonus != 0) totals.Add(new[] { "Eingelöstes Bonus-Guthaben " + Money(bonus) });
        totals.Add(new[] { "Zahlungsbetrag " + Money(total + bonus) });
        totals.Add(new[] { "Du bezahlst per Lastschrift " + Money(total + bonus) });
        totals.Add(new[] { "* Kein Rabatt oder Guthaben auf diese Produkte." });
        var page2 = new List<string[]> { tableHead }.Concat(lines.Skip(split).Select(Cells)).Concat(totals).Append(footer).Append(new[] { "Seite 2 von 2" }).ToList();
        return Build([page1, page2]);
    }

    private static string[] Cells(Line l) => [l.Name, l.Quantity, l.Vat, l.Unit, l.Total];

    /// <summary>
    /// A PDF whose pages hold the rows: single-cell rows at the left margin, five-cell rows in table columns. Text is
    /// Helvetica in WinAnsi encoding (umlauts, "€" and the no-break space included).
    /// </summary>
    public static byte[] Build(IReadOnlyList<IReadOnlyList<string[]>> pages)
    {
        double[] columns = [40, 330, 375, 410, 490];
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // the page tree, filled in below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var kids = new List<int>();
        foreach (var rows in pages)
        {
            var content = new StringBuilder();
            var y = 800;
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                    content.Append(CultureInfo.InvariantCulture, $"BT /F1 9 Tf {(row.Length == 1 ? 40 : columns[i])} {y} Td ({Escape(row[i])}) Tj ET\n");
                y -= 14;
            }
            objects.Add($"<< /Length {content.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n{content}endstream");
            var contentId = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {contentId} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            kids.Add(objects.Count);
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => k.ToString(CultureInfo.InvariantCulture) + " 0 R"))}] /Count {kids.Count} >>";

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>The text as a PDF string body: WinAnsi bytes, everything outside printable ASCII as octal escapes.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            int code = c switch
            {
                '€' => 0x80,
                _ when c <= 0xFF => c,
                _ => '?',
            };
            if (code is '(' or ')' or '\\') sb.Append('\\').Append((char)code);
            else if (code is < 0x20 or > 0x7E) sb.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
            else sb.Append((char)code);
        }
        return sb.ToString();
    }
}
