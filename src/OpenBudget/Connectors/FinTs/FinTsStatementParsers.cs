using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenBudget.Connectors.FinTs;

/// <summary>Number and date helpers for the formats banks use.</summary>
internal static class FinTsFormats
{
    /// <summary>"1234,56" or "1234," (German decimal comma) to a decimal.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace(',', '.');
        if (normalized.EndsWith('.', StringComparison.Ordinal)) normalized += "0";
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public static DateOnly? ParseDate(string? yyyymmdd) =>
        DateOnly.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string FormatDate(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>Reads camt.052 / camt.053 documents (any version) into transactions.</summary>
internal static class CamtParser
{
    /// <summary>
    /// Entries of one document. <paramref name="bookedDocument"/> says where the bank put it: entries of a booked
    /// document count when they are booked (BOOK or no status), entries of a not-booked document when they are pending.
    /// </summary>
    public static List<RemoteTransaction> Parse(string xml, bool bookedDocument)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FinTsProtocolException("The bank sent an unreadable camt document.", ex);
        }

        var result = new List<RemoteTransaction>();
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "Ntry"))
        {
            var status = Text(entry, "Sts") ?? Text(entry, "Sts", "Cd");
            var booked = status is null || string.Equals(status, "BOOK", StringComparison.OrdinalIgnoreCase);
            if (booked != bookedDocument) continue;

            var amountElement = Child(entry, "Amt");
            if (amountElement == null || !decimal.TryParse(amountElement.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) continue;
            var credit = !string.Equals(Text(entry, "CdtDbtInd"), "DBIT", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(Text(entry, "RvslInd"), "true", StringComparison.OrdinalIgnoreCase)) credit = !credit;
            var signed = credit ? amount : -amount;

            var bookingDate = Date(entry, "BookgDt") ?? Date(entry, "ValDt");
            if (bookingDate == null) continue;
            var details = entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "TxDtls");
            var counterparty = details == null ? null : (credit ? PartyName(details, "Dbtr") : PartyName(details, "Cdtr"));
            var purpose = details == null ? null : string.Join(' ', details.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()).Where(v => v.Length > 0));
            if (string.IsNullOrEmpty(purpose)) purpose = Text(entry, "AddtlNtryInf");
            var reference = Text(entry, "AcctSvcrRef");
            if (string.Equals(reference, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase) || string.Equals(reference, "NONREF", StringComparison.OrdinalIgnoreCase)) reference = null;

            result.Add(new RemoteTransaction(
                signed,
                amountElement.Attribute("Ccy")?.Value ?? "EUR",
                bookingDate.Value,
                Date(entry, "ValDt"),
                string.IsNullOrWhiteSpace(counterparty) ? null : counterparty.Trim(),
                string.IsNullOrWhiteSpace(purpose) ? null : purpose,
                booked,
                reference));
        }
        return result;
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement parent, params string[] path)
    {
        var current = parent;
        foreach (var name in path)
        {
            current = Child(current, name);
            if (current == null) return null;
        }
        var value = current.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static DateOnly? Date(XElement entry, string name)
    {
        var container = Child(entry, name);
        if (container == null) return null;
        var text = (Child(container, "Dt") ?? Child(container, "DtTm"))?.Value;
        if (string.IsNullOrEmpty(text) || text.Length < 10) return null;
        return DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string? PartyName(XElement details, string role)
    {
        var party = details.Descendants().FirstOrDefault(e => e.Name.LocalName == role);
        return party?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nm")?.Value;
    }
}

/// <summary>Reads MT940 statements (HKKAZ) into transactions.</summary>
internal static partial class Mt940Parser
{
    [GeneratedRegex(@"^(?<vd>\d{6})(?<bd>\d{4})?(?<mark>RC|RD|EC|ED|C|D)(?<fund>[A-Z])?(?<amt>\d+,\d*)N(?<code>[A-Z0-9]{3})(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"(?<tag>EREF|KREF|MREF|CRED|DEBT|SVWZ|ABWA|ABWE|IBAN|BIC|COAM|OAMT|ORDP|BENM|PURP)\+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PurposeTag();

    public static List<RemoteTransaction> Parse(string mt940, string defaultCurrency, bool booked)
    {
        var result = new List<RemoteTransaction>();
        var currency = defaultCurrency;
        string? entryLine = null;
        var infoLines = new List<string>();

        void Flush()
        {
            if (entryLine == null) return;
            var tx = Build(entryLine, string.Concat(infoLines), currency, booked);
            if (tx != null) result.Add(tx);
            entryLine = null;
            infoLines.Clear();
        }

        var tag = string.Empty;
        var value = new StringBuilder();
        void EndTag()
        {
            var text = value.ToString();
            value.Clear();
            switch (tag)
            {
                case "60F" or "60M":
                    if (text.Length >= 10) currency = text.Substring(7, 3);
                    break;
                case "61":
                    Flush();
                    entryLine = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0];
                    break;
                case "86":
                    infoLines.Add(text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal));
                    break;
                default:
                    break;
            }
        }

        foreach (var raw in mt940.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length >= 4 && line[0] == ':' && line.IndexOf(':', 1, StringComparison.Ordinal) is var close and > 1 && close <= 4)
            {
                EndTag();
                tag = line[1..close];
                value.Append(line[(close + 1)..]);
            }
            else if (line is "-" || line.StartsWith("-}", StringComparison.Ordinal))
            {
                EndTag();
                tag = string.Empty;
            }
            else if (tag.Length > 0)
            {
                value.Append('\n').Append(line);
            }
        }
        EndTag();
        Flush();
        return result;
    }

    private static RemoteTransaction? Build(string entryLine, string info, string currency, bool booked)
    {
        var m = EntryLine().Match(entryLine);
        if (!m.Success) return null;
        var valueDate = new DateOnly(2000 + int.Parse(m.Groups["vd"].Value[..2], CultureInfo.InvariantCulture), int.Parse(m.Groups["vd"].Value.AsSpan(2, 2), CultureInfo.InvariantCulture), int.Parse(m.Groups["vd"].Value.AsSpan(4, 2), CultureInfo.InvariantCulture));
        var bookingDate = valueDate;
        if (m.Groups["bd"].Success)
        {
            var month = int.Parse(m.Groups["bd"].Value.AsSpan(0, 2), CultureInfo.InvariantCulture);
            var day = int.Parse(m.Groups["bd"].Value.AsSpan(2, 2), CultureInfo.InvariantCulture);
            var year = valueDate.Year;
            if (month - valueDate.Month > 6) year--;
            else if (valueDate.Month - month > 6) year++;
            if (day >= 1 && day <= DateTime.DaysInMonth(year, Math.Clamp(month, 1, 12))) bookingDate = new DateOnly(year, Math.Clamp(month, 1, 12), day);
        }
        var amount = FinTsFormats.ParseAmount(m.Groups["amt"].Value) ?? 0m;
        var mark = m.Groups["mark"].Value;
        var credit = mark is "C" or "RD" or "EC";
        var signed = credit ? amount : -amount;

        var rest = m.Groups["rest"].Value;
        var slashes = rest.IndexOf("//", StringComparison.Ordinal);
        var bankReference = slashes >= 0 ? rest[(slashes + 2)..].Trim() : null;
        if (string.IsNullOrEmpty(bankReference) || bankReference.Equals("NONREF", StringComparison.OrdinalIgnoreCase)) bankReference = null;

        var (name, purpose) = ParseInfo(info);
        return new RemoteTransaction(signed, currency, bookingDate, valueDate, name, purpose, booked, bankReference);
    }

    private static (string? Name, string? Purpose) ParseInfo(string info)
    {
        if (info.Length == 0) return (null, null);
        if (!info.Contains('?', StringComparison.Ordinal)) return (null, info.Trim());
        var fields = new SortedDictionary<int, string>();
        foreach (var part in info.Split('?').Skip(1))
        {
            if (part.Length >= 2 && int.TryParse(part.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                fields[n] = fields.GetValueOrDefault(n) + part[2..];
        }
        var purposeText = string.Concat(fields.Where(f => f.Key is (>= 20 and <= 29) or (>= 60 and <= 63)).Select(f => f.Value));
        var name = string.Join(' ', new[] { fields.GetValueOrDefault(32), fields.GetValueOrDefault(33) }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        return (name.Length == 0 ? null : name, ExtractPurpose(purposeText));
    }

    private static string? ExtractPurpose(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = PurposeTag().Matches(text);
        if (matches.Count == 0) return text.Trim();
        for (var i = 0; i < matches.Count; i++)
        {
            if (!string.Equals(matches[i].Groups["tag"].Value, "SVWZ", StringComparison.Ordinal)) continue;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            return text[start..end].Trim();
        }
        return text.Trim();
    }
}

/// <summary>Reads MT535 custody statements (HKWPD) into positions.</summary>
internal static class Mt535Parser
{
    public static List<RemotePosition> Parse(string mt535)
    {
        var positions = new List<RemotePosition>();
        var lines = JoinTags(mt535);
        List<string>? block = null;
        foreach (var line in lines)
        {
            if (line.StartsWith(":16R:FIN", StringComparison.Ordinal)) block = [];
            else if (line.StartsWith(":16S:FIN", StringComparison.Ordinal))
            {
                if (block != null && Build(block) is { } p) positions.Add(p);
                block = null;
            }
            else block?.Add(line);
        }
        return positions;
    }

    /// <summary>Tag lines with their continuation lines folded in (continuations joined by a newline).</summary>
    private static List<string> JoinTags(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line == "-") continue;
            if (line[0] == ':' || lines.Count == 0) lines.Add(line);
            else lines[^1] += "\n" + line;
        }
        return lines;
    }

    private static RemotePosition? Build(List<string> block)
    {
        string? isin = null, wkn = null, name = null, currency = null;
        decimal? quantity = null, price = null, value = null, purchase = null;
        foreach (var line in block)
        {
            if (line.StartsWith(":35B:", StringComparison.Ordinal))
            {
                var parts = line[5..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var first = parts[0].Trim();
                if (first.StartsWith("ISIN ", StringComparison.Ordinal))
                {
                    var rest = first[5..].Split('/');
                    isin = rest[0].Trim();
                    if (rest.Length >= 3 && rest[2].Trim().Length > 0) wkn = rest[2].Trim();
                }
                name = string.Join(' ', parts.Skip(1).Select(p => p.Trim()));
            }
            else if (line.StartsWith(":93B::AGGR//", StringComparison.Ordinal) || line.StartsWith(":93B::", StringComparison.Ordinal) && line.Contains("//UNIT/", StringComparison.Ordinal))
            {
                var slash = line.LastIndexOf('/', StringComparison.Ordinal);
                quantity ??= FinTsFormats.ParseAmount(line[(slash + 1)..].Replace("N", "-", StringComparison.Ordinal));
            }
            else if (line.StartsWith(":90B::MRKT//ACTU/", StringComparison.Ordinal))
            {
                (currency, price) = CurrencyAmount(line[17..]);
            }
            else if (line.StartsWith(":90B::AVGE//ACTU/", StringComparison.Ordinal))
            {
                purchase = CurrencyAmount(line[17..]).Amount;
            }
            else if (line.StartsWith(":90A::MRKT//PRCT/", StringComparison.Ordinal))
            {
                price ??= FinTsFormats.ParseAmount(line[17..]);
            }
            else if (line.StartsWith(":19A::HOLD//", StringComparison.Ordinal))
            {
                var (cur, amount) = CurrencyAmount(line[12..]);
                value = amount;
                currency ??= cur;
            }
        }
        if (quantity == null || (isin == null && name == null)) return null;
        value ??= price != null ? quantity * price : null;
        return new RemotePosition(name is { Length: > 0 } ? name : isin ?? "Position", isin, wkn, null, quantity.Value, price, currency, value, purchase);
    }

    private static (string? Currency, decimal? Amount) CurrencyAmount(string text)
    {
        var t = text.Trim();
        var negative = t.StartsWith('N', StringComparison.Ordinal);
        if (negative) t = t[1..];
        if (t.Length < 4) return (null, null);
        var amount = FinTsFormats.ParseAmount(t[3..]);
        return (t[..3], negative ? -amount : amount);
    }
}

/// <summary>A balance from HISAL: the booked balance and, when the bank sends it, the one including pending items.</summary>
internal sealed record FinTsBalance(decimal Booked, decimal? WithPending)
{
    public static FinTsBalance? FromSegment(FinTsSegment hisal)
    {
        var booked = Read(hisal.Deg(3));
        if (booked == null) return null;
        return new FinTsBalance(booked.Value, Read(hisal.Deg(4)));
    }

    private static decimal? Read(IReadOnlyList<string> deg)
    {
        if (deg.Count < 2) return null;
        var amount = FinTsFormats.ParseAmount(deg[1]);
        if (amount == null) return null;
        return string.Equals(deg[0], "D", StringComparison.Ordinal) ? -amount : amount;
    }
}
