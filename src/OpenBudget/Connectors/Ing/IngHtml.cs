using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.Ing;

/// <summary>A form field as a browser would submit it.</summary>
internal sealed record HtmlField(string Name, string Value);

/// <summary>An HTML form: its id, action (still relative) and the fields a browser would submit as they stand.</summary>
internal sealed record HtmlForm(string? Id, string Action, IReadOnlyList<HtmlField> Fields, string Inner);

/// <summary>A Wicket Ajax call wired to a component: <c>Wicket.Ajax.ajax({"u":..,"m":"POST","c":..,"f":..,"sc":..})</c>.</summary>
internal sealed record WicketAjaxCall(string Url, bool Post, string ComponentId, string? FormId, string? SubmittingName);

/// <summary>One account row of the web banking overview ("Meine Konten").</summary>
internal sealed record IngOverviewAccount(string Headline, string Product, string? Iban, decimal? Balance, string Href);

/// <summary>One row of a Postbox listing.</summary>
internal sealed record IngPostboxRow(string Type, string Subject, DateOnly Date, string DownloadHref);

/// <summary>A postbox tab (one per customer / joint customer relationship).</summary>
internal sealed record IngPostboxTab(string Label, string Href, bool Selected);

/// <summary>Parsed CSV export of the transaction list ("Umsatzanzeige").</summary>
internal sealed record IngCsv(decimal? Balance, IReadOnlyList<RemoteTransaction> Transactions);

/// <summary>
/// Reading ING's web banking pages (Apache Wicket, server-rendered). There is no HTML parser in the app, so these are
/// focused, attribute-order independent regular expressions over the few structures the connector needs.
/// </summary>
internal static partial class IngHtml
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Decode(string text) => WebUtility.HtmlDecode(text);

    /// <summary>Text content: tags removed, entities decoded, whitespace collapsed.</summary>
    public static string Text(string html) => Whitespace().Replace(Decode(Tags().Replace(html, " ")), " ").Trim();

    /// <summary>The value of attribute <paramref name="name"/> in an opening tag, decoded; null when absent.</summary>
    public static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, @"\s" + Regex.Escape(name) + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        return m.Success ? Decode(m.Groups["v"].Value) : null;
    }

    private static bool HasAttr(string tag, string name) =>
        Regex.IsMatch(tag, @"\s" + Regex.Escape(name) + @"(\s|=|/?>)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static bool HasClass(string tag, string cls) =>
        (Attr(tag, "class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls, StringComparer.Ordinal);

    /// <summary>Every form in <paramref name="html"/>.</summary>
    public static IReadOnlyList<HtmlForm> Forms(string html)
    {
        var forms = new List<HtmlForm>();
        foreach (Match m in FormRegex().Matches(html))
        {
            var open = m.Groups["open"].Value;
            var inner = m.Groups["inner"].Value;
            forms.Add(new HtmlForm(Attr(open, "id"), Attr(open, "action") ?? "", Fields(inner), inner));
        }
        return forms;
    }

    /// <summary>The first form with a field (or button) named <paramref name="fieldName"/>.</summary>
    public static HtmlForm? FormWith(string html, string fieldName) =>
        Forms(html).FirstOrDefault(f => f.Inner.Contains("name=\"" + fieldName + "\"", StringComparison.Ordinal));

    /// <summary>The fields a browser would submit (no buttons, only checked radios/checkboxes, no disabled ones).</summary>
    public static IReadOnlyList<HtmlField> Fields(string inner)
    {
        var fields = new List<HtmlField>();
        foreach (Match m in InputRegex().Matches(inner))
        {
            var tag = m.Value;
            var name = Attr(tag, "name");
            if (string.IsNullOrEmpty(name) || HasAttr(tag, "disabled")) continue;
            var type = (Attr(tag, "type") ?? "text").ToUpperInvariant();
            if (type is "SUBMIT" or "BUTTON" or "IMAGE" or "RESET" or "FILE") continue;
            if (type is "RADIO" or "CHECKBOX")
            {
                if (!HasAttr(tag, "checked")) continue;
                fields.Add(new HtmlField(name, Attr(tag, "value") ?? "on"));
                continue;
            }
            fields.Add(new HtmlField(name, Attr(tag, "value") ?? ""));
        }
        foreach (Match m in SelectRegex().Matches(inner))
        {
            var open = m.Groups["open"].Value;
            var name = Attr(open, "name");
            if (string.IsNullOrEmpty(name) || HasAttr(open, "disabled")) continue;
            var options = SelectOptions(m.Groups["inner"].Value);
            var chosen = options.FirstOrDefault(o => o.Selected) ?? options.FirstOrDefault();
            if (chosen != null) fields.Add(new HtmlField(name, chosen.Value));
        }
        return fields;
    }

    public sealed record SelectOption(string Value, string Label, bool Selected);

    private static List<SelectOption> SelectOptions(string inner) =>
        OptionRegex().Matches(inner)
            .Select(o => new SelectOption(Attr(o.Groups["open"].Value, "value") ?? Text(o.Groups["label"].Value), Text(o.Groups["label"].Value), HasAttr(o.Groups["open"].Value, "selected")))
            .ToList();

    /// <summary>The id and options of the select named <paramref name="name"/>, or null.</summary>
    public static (string? Id, IReadOnlyList<SelectOption> Options)? Select(string html, string name)
    {
        foreach (Match m in SelectRegex().Matches(html))
        {
            var open = m.Groups["open"].Value;
            if (string.Equals(Attr(open, "name"), name, StringComparison.Ordinal))
                return (Attr(open, "id"), SelectOptions(m.Groups["inner"].Value));
        }
        return null;
    }

    /// <summary>Radio buttons named <paramref name="name"/> with the text of their label (value, label).</summary>
    public static IReadOnlyList<(string Value, string Label)> Radios(string html, string name)
    {
        var result = new List<(string, string)>();
        foreach (Match m in RadioLabelRegex().Matches(html))
        {
            var tag = m.Groups["input"].Value;
            if (!string.Equals(Attr(tag, "name"), name, StringComparison.Ordinal)) continue;
            result.Add((Attr(tag, "value") ?? "", Text(m.Groups["label"].Value)));
        }
        return result;
    }

    /// <summary>Every <c>Wicket.Ajax.ajax({...})</c> call in a page or Ajax response.</summary>
    public static IReadOnlyList<WicketAjaxCall> AjaxCalls(string html)
    {
        var calls = new List<WicketAjaxCall>();
        const string marker = "Wicket.Ajax.ajax({";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        while (at >= 0)
        {
            var next = html.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            var end = next < 0 ? Math.Min(html.Length, at + 2000) : Math.Min(next, at + 2000);
            var body = html[at..end];
            var u = JsonString(body, "u");
            var c = JsonString(body, "c");
            if (u != null && c != null)
                calls.Add(new WicketAjaxCall(u, string.Equals(JsonString(body, "m"), "POST", StringComparison.OrdinalIgnoreCase), c, JsonString(body, "f"), JsonString(body, "sc")));
            at = next;
        }
        return calls;
    }

    private static string? JsonString(string body, string key)
    {
        var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\":\"(?<v>[^\"]*)\"", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups["v"].Value.Replace("\\/", "/", StringComparison.Ordinal) : null;
    }

    /// <summary>The Ajax call bound to component <paramref name="componentId"/>, or null.</summary>
    public static WicketAjaxCall? AjaxFor(string html, string componentId) =>
        AjaxCalls(html).FirstOrDefault(c => string.Equals(c.ComponentId, componentId, StringComparison.Ordinal));

    /// <summary>The download URL of a <c>Wicket.AjaxDownload.initiate(...)</c>, or null.</summary>
    public static string? AjaxDownloadUrl(string response)
    {
        var m = AjaxDownloadRegex().Match(response);
        return m.Success ? m.Groups["u"].Value.Replace("\\/", "/", StringComparison.Ordinal) : null;
    }

    /// <summary>The href of the first link with CSS class <paramref name="cls"/>, or null.</summary>
    public static string? LinkWithClass(string html, string cls)
    {
        foreach (Match m in AnchorRegex().Matches(html))
        {
            if (HasClass(m.Groups["open"].Value, cls)) return Attr(m.Groups["open"].Value, "href");
        }
        return null;
    }

    /// <summary>The id of the first link whose text is <paramref name="text"/>, or null.</summary>
    public static string? LinkIdByText(string html, string text)
    {
        foreach (Match m in AnchorRegex().Matches(html))
        {
            if (string.Equals(Text(m.Groups["inner"].Value), text, StringComparison.OrdinalIgnoreCase)) return Attr(m.Groups["open"].Value, "id");
        }
        return null;
    }

    // ---- account overview ----

    public static bool IsOverview(string html) => html.Contains("g2p-account__row", StringComparison.Ordinal) || html.Contains("quicklinks__postbox", StringComparison.Ordinal);

    public static IReadOnlyList<IngOverviewAccount> OverviewAccounts(string html)
    {
        var rows = new List<IngOverviewAccount>();
        foreach (Match m in AnchorRegex().Matches(html))
        {
            var open = m.Groups["open"].Value;
            if (!HasClass(open, "g2p-account__row")) continue;
            var inner = m.Groups["inner"].Value;
            var headline = Match1(inner, @"g2p-account__headline""[^>]*>\s*<span>(?<v>[^<]*)</span>");
            if (headline == null) continue;
            var product = Match1(inner, @"g2p-banking-header__account__type""[^>]*>\s*\(\s*<span>(?<v>[^<]*)</span>");
            var iban = Match1(inner, @"g2p-account__iban""[^>]*>(?<v>[^<]*)<");
            var amount = Match1(inner, @"Betrag\s+(?<v>-?\s*[\d.]+,\d{2})\s+Euro");
            rows.Add(new IngOverviewAccount(
                Text(headline), Text(product ?? headline), NormalizeIban(iban), amount == null ? null : ParseAmount(amount), Attr(open, "href") ?? ""));
        }
        return rows;
    }

    private static string? Match1(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.ExplicitCapture | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>An IBAN without spaces, upper case; null for anything that is not one.</summary>
    public static string? NormalizeIban(string? text)
    {
        if (text == null) return null;
        var compact = Regex.Replace(Decode(text), @"\s", "", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();
        return IbanRegex().IsMatch(compact) ? compact : null;
    }

    /// <summary>The first IBAN written anywhere in <paramref name="text"/> (with or without the usual spaces), or null.</summary>
    public static string? FindIban(string text)
    {
        var m = IbanInTextRegex().Match(text);
        return m.Success ? NormalizeIban(m.Value) : null;
    }

    public static decimal ParseAmount(string text) =>
        decimal.Parse(Regex.Replace(text, @"\s", "", RegexOptions.None, TimeSpan.FromSeconds(1)), NumberStyles.Number | NumberStyles.AllowLeadingSign, German);

    // ---- postbox ----

    public static IReadOnlyList<IngPostboxTab> PostboxTabs(string html)
    {
        var nav = Match1(html, @"<div class=""chips-nav"">(?<v>.*?)</div>");
        if (nav == null) return [];
        return AnchorRegex().Matches(nav)
            .Select(m => new IngPostboxTab(Text(m.Groups["inner"].Value), Attr(m.Groups["open"].Value, "href") ?? "", HasClass(m.Groups["open"].Value, "selected")))
            .Where(t => t.Href.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<IngPostboxRow> PostboxRows(string html)
    {
        var rows = new List<IngPostboxRow>();
        var starts = PostboxRowStartRegex().Matches(html).Select(m => m.Index).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            var block = html[starts[i]..end];
            var type = Match1(block, @"postbox-grid-left[^""]*""[^>]*>.*?<span>(?<v>[^<]*)</span>");
            var subject = Match1(block, @"postbox-grid-description[^""]*""[^>]*>(?<v>.*?)</span>");
            var date = Match1(block, @"postbox-grid-right[^""]*""[^>]*>\s*(?<v>\d{2}\.\d{2}\.\d{4})");
            string? href = null;
            foreach (Match a in AnchorRegex().Matches(block))
            {
                if (string.Equals(Text(a.Groups["inner"].Value), "Download", StringComparison.OrdinalIgnoreCase))
                {
                    href = Attr(a.Groups["open"].Value, "href");
                    break;
                }
            }
            if (type == null || date == null || string.IsNullOrEmpty(href) || href == "#") continue;
            if (!DateOnly.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            rows.Add(new IngPostboxRow(Text(type), Text(subject ?? ""), day, href));
        }
        return rows;
    }

    // ---- transaction CSV ----

    /// <summary>The bytes of a CSV export as text: UTF-8 when it is valid UTF-8 (BOM or not), otherwise ISO-8859-1 (ING's classic export).</summary>
    public static string DecodeCsv(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// ING's CSV export: a few "key;value" header lines (among them <c>Saldo;1.234,56;EUR</c>), then the column header
    /// <c>Buchung;Wertstellungsdatum;Auftraggeber/Empfänger;Buchungstext;Verwendungszweck;[Referenz;]Saldo;Währung;Betrag;Währung</c>
    /// and one booked transaction per line. Columns are found by name, so the optional ones may come and go.
    /// </summary>
    public static IngCsv ParseCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        decimal? balance = null;
        var header = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var cells = SplitCsv(lines[i]);
            if (cells.Count >= 2 && string.Equals(cells[0], "Saldo", StringComparison.Ordinal) && TryAmount(cells[1], out var b)) balance = b;
            if (cells.Count > 3 && string.Equals(cells[0], "Buchung", StringComparison.Ordinal))
            {
                header = i;
                break;
            }
        }
        if (header < 0) throw new ConnectorException("ING's transaction export has an unexpected format.");

        var columns = SplitCsv(lines[header]);
        int Col(params string[] names) => columns.FindIndex(c => names.Contains(c, StringComparer.OrdinalIgnoreCase));
        var booking = Col("Buchung");
        var value = Col("Wertstellungsdatum", "Valuta");
        var name = Col("Auftraggeber/Empfänger");
        var text2 = Col("Buchungstext");
        var purpose = Col("Verwendungszweck");
        var reference = Col("Referenz");
        var amount = Col("Betrag");
        if (booking < 0 || amount < 0) throw new ConnectorException("ING's transaction export lacks the booking date or amount column.");
        var currency = amount + 1 < columns.Count && columns[amount + 1].Length > 0 && columns[amount + 1][0] == 'W' ? amount + 1 : -1;

        var transactions = new List<RemoteTransaction>();
        foreach (var line in lines.Skip(header + 1))
        {
            if (line.Trim().Length == 0) continue;
            var cells = SplitCsv(line);
            string? Cell(int index) => index >= 0 && index < cells.Count && cells[index].Trim().Length > 0 ? cells[index].Trim() : null;
            if (!TryDate(Cell(booking), out var bookingDate) || !TryAmount(Cell(amount), out var sum)) continue;
            DateOnly? valueDate = TryDate(Cell(value), out var vd) ? vd : null;
            transactions.Add(new RemoteTransaction(
                sum, Cell(currency) ?? "EUR", bookingDate, valueDate, Cell(name), Cell(purpose), Booked: true,
                BankTransactionId: Cell(reference), BookingText: Cell(text2)));
        }
        return new IngCsv(balance, transactions);
    }

    private static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text ?? "", "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryAmount(string? text, out decimal amount) =>
        decimal.TryParse((text ?? "").Replace(" ", "", StringComparison.Ordinal), NumberStyles.Number | NumberStyles.AllowLeadingSign, German, out amount);

    /// <summary>Splits one CSV line on ';', honouring double quotes ("" inside quotes is a quote).</summary>
    public static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var skipQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (skipQuote)
            {
                skipQuote = false;
                continue;
            }
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    skipQuote = true;
                }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells;
    }

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<open><form\b[^>]*>)(?<inner>.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FormRegex();

    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InputRegex();

    [GeneratedRegex(@"(?<open><select\b[^>]*>)(?<inner>.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SelectRegex();

    [GeneratedRegex(@"(?<open><option\b[^>]*>)(?<label>[^<]*)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"(?<input><input\b[^>]*type=""radio""[^>]*>).*?radio--label"">(?<label>[^<]*)<", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RadioLabelRegex();

    [GeneratedRegex(@"(?<open><a\b[^>]*>)(?<inner>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"Wicket\.AjaxDownload\.initiate\(.*?""downloadUrl"":""(?<u>[^""]+)""", RegexOptions.Singleline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AjaxDownloadRegex();

    [GeneratedRegex(@"<div\b[^>]*class=""[^""]*\bpostbox-message\b[^""]*""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PostboxRowStartRegex();

    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{10,30}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IbanInTextRegex();
}
