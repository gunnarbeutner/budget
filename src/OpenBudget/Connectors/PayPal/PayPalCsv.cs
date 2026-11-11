using System.Globalization;
using System.Text;

namespace OpenBudget.Connectors.PayPal;

/// <summary>One row of PayPal's activity download (CSV), already parsed. <see cref="Index"/> is the row's position in the file.</summary>
internal sealed record PayPalRow(
    int Index,
    DateOnly Date,
    TimeOnly? Time,
    string? Name,
    string? Type,
    string? Status,
    string Currency,
    decimal Gross,
    decimal Fee,
    decimal Net,
    string? FromEmail,
    string? ToEmail,
    string? TransactionId,
    string? ReferenceId,
    string? ItemTitle,
    string? Subject,
    string? Note,
    decimal? Balance,
    string? BalanceImpact);

/// <summary>
/// Reads PayPal's "Download activity" CSV (English or German headers, comma/semicolon/tab separated, amounts with either
/// decimal separator) and turns its rows into transactions of one balance currency.
/// </summary>
internal static class PayPalCsv
{
    // Column names as PayPal writes them (English and German exports, older and newer layouts), lower-case.
    private static readonly Dictionary<string, string[]> Columns = new(StringComparer.Ordinal)
    {
        ["date"] = ["date", "datum"],
        ["time"] = ["time", "uhrzeit"],
        ["name"] = ["name"],
        ["type"] = ["type", "typ", "beschreibung", "description"],
        ["status"] = ["status"],
        ["currency"] = ["currency", "währung"],
        ["gross"] = ["gross", "brutto"],
        ["fee"] = ["fee", "gebühr", "entgelt"],
        ["net"] = ["net", "netto"],
        ["from"] = ["from email address", "absender e-mail-adresse"],
        ["to"] = ["to email address", "empfänger e-mail-adresse"],
        ["id"] = ["transaction id", "transaktionscode"],
        ["ref"] = ["reference txn id", "zugehöriger transaktionscode"],
        ["item"] = ["item title", "artikelbezeichnung"],
        ["balance"] = ["balance", "guthaben"],
        ["subject"] = ["subject", "betreff"],
        ["note"] = ["note", "hinweis"],
        ["impact"] = ["balance impact", "auswirkung auf guthaben"],
    };

    /// <summary>Statuses of payments that are not final yet (reported unbooked).</summary>
    private static readonly string[] PendingStatuses = ["pending", "ausstehend", "offen", "in bearbeitung", "unclaimed", "nicht abgeholt"];

    /// <summary>Row types that never move the balance (only used when the file has no balance-impact column).</summary>
    private static readonly string[] MemoTypes = ["authorization", "autorisierung", "order", "bestellung"];

    /// <summary>Row types of a currency conversion.</summary>
    private static readonly string[] ConversionTypes = ["currency conversion", "währungsumrechnung"];

    /// <summary>True when the text looks like an activity CSV (has a date, a currency and an amount column).</summary>
    public static bool LooksLikeCsv(string text)
    {
        var header = FirstLine(text);
        var names = Split(header, Delimiter(header)).Select(Normalize).ToList();
        return Find(names, "date") >= 0 && Find(names, "currency") >= 0 && (Find(names, "gross") >= 0 || Find(names, "net") >= 0);
    }

    /// <summary>The rows of the file; rows without a date, currency or amount are skipped.</summary>
    public static List<PayPalRow> Parse(string text)
    {
        var records = Records(text.TrimStart('﻿'));
        var rows = new List<PayPalRow>();
        if (records.Count == 0) return rows;
        var names = records[0].Select(Normalize).ToList();
        var col = Columns.Keys.ToDictionary(k => k, k => Find(names, k), StringComparer.Ordinal);
        string? Get(List<string> r, string key) =>
            col[key] is var i and >= 0 && i < r.Count && r[i].Trim() is { Length: > 0 } v ? v : null;

        var data = records.Skip(1).Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
        var dayFirst = DayFirst(data.Select(r => Get(r, "date")));
        for (var n = 0; n < data.Count; n++)
        {
            var r = data[n];
            if (ParseDate(Get(r, "date"), dayFirst) is not { } date) continue;
            if (Get(r, "currency")?.ToUpperInvariant() is not { Length: 3 } currency) continue;
            var gross = ParseAmount(Get(r, "gross"));
            var fee = ParseAmount(Get(r, "fee")) ?? 0m;
            var net = ParseAmount(Get(r, "net"));
            if (gross == null && net == null) continue;
            gross ??= net!.Value - fee;
            net ??= gross.Value + fee;
            TimeOnly? time = TimeOnly.TryParse(Get(r, "time") ?? "", CultureInfo.InvariantCulture, out var t) ? t : null;
            rows.Add(new PayPalRow(n, date, time, Get(r, "name"), Get(r, "type"), Get(r, "status"), currency,
                gross.Value, fee, net.Value, Get(r, "from"), Get(r, "to"), Get(r, "id"), Get(r, "ref"), Get(r, "item"),
                Get(r, "subject"), Get(r, "note"), ParseAmount(Get(r, "balance")), Get(r, "impact")));
        }
        return rows;
    }

    /// <summary>Whether a row moves the balance: "Memo" rows (authorizations, holds, orders) do not.</summary>
    public static bool AffectsBalance(PayPalRow row)
    {
        if (row.BalanceImpact is { } impact) return !impact.Equals("memo", StringComparison.OrdinalIgnoreCase);
        return row.Type is not { } type || !MemoTypes.Any(m => type.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsConversion(PayPalRow row) =>
        row.Type is { } type && ConversionTypes.Any(c => type.Contains(c, StringComparison.OrdinalIgnoreCase));

    public static bool IsPending(PayPalRow row) =>
        row.Status is { } s && PendingStatuses.Any(p => s.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The currencies of the balance-affecting rows.</summary>
    public static IEnumerable<string> Currencies(IEnumerable<PayPalRow> rows) =>
        rows.Where(AffectsBalance).Select(r => r.Currency).Distinct(StringComparer.Ordinal);

    /// <summary>The balance after the newest balance-affecting row of <paramref name="currency"/> that names one, or null.</summary>
    public static decimal? LatestBalance(IEnumerable<PayPalRow> rows, string currency) =>
        rows.Where(r => r.Currency == currency && r.Balance != null && AffectsBalance(r))
            .OrderBy(r => r.Date).ThenBy(r => r.Time ?? TimeOnly.MinValue).ThenBy(r => r.Index)
            .LastOrDefault()?.Balance;

    /// <summary>
    /// The transactions of one balance currency. Each payment is its gross amount; a fee is a separate line (id
    /// "&lt;transaction id&gt;:fee"), so both together make the net amount the balance moved by. Rows that do not move the
    /// balance are left out, and so is a payment in this currency together with the conversion that paid for it (the
    /// two cancel out here; the conversion row in the funding currency carries the payment). A conversion row without a
    /// name of its own gets the name and purpose of the payment it belongs to. Each transaction id counts once.
    /// </summary>
    public static List<RemoteTransaction> Map(IReadOnlyList<PayPalRow> rows, string currency, DateOnly? since)
    {
        var moving = rows.Where(AffectsBalance).ToList();
        var byId = new Dictionary<string, PayPalRow>(StringComparer.Ordinal);
        foreach (var r in moving.Where(r => r.TransactionId != null && !IsConversion(r))) byId.TryAdd(r.TransactionId!, r);

        // A payment in this currency and the conversion row in this currency that refers to it, netting to zero.
        var offset = new HashSet<int>();
        foreach (var conv in moving.Where(r => r.Currency == currency && IsConversion(r) && r.ReferenceId != null))
        {
            if (byId.TryGetValue(conv.ReferenceId!, out var payment) && payment.Currency == currency
                && payment.Net + conv.Net == 0 && !offset.Contains(payment.Index))
            {
                offset.Add(payment.Index);
                offset.Add(conv.Index);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteTransaction>();
        foreach (var r in moving.Where(r => r.Currency == currency && !offset.Contains(r.Index)))
        {
            if (since is { } s && r.Date < s) continue;
            if (r.TransactionId is { } id && !seen.Add(id)) continue;
            var source = IsConversion(r) && r.Name == null && r.ReferenceId != null && byId.TryGetValue(r.ReferenceId, out var p) ? p : r;
            var booked = !IsPending(r);
            var name = source.Name ?? (source.Gross < 0 ? source.ToEmail : source.FromEmail) ?? source.Type;
            var purpose = Purpose(source, r);
            result.Add(new RemoteTransaction(r.Gross, currency, r.Date, null, name, purpose, booked,
                BankTransactionId: r.TransactionId, BookingText: r.Type));
            if (r.Fee != 0)
            {
                result.Add(new RemoteTransaction(r.Fee, currency, r.Date, null, "PayPal",
                    r.TransactionId != null ? $"Fee ({r.TransactionId})" : "Fee", booked,
                    BankTransactionId: r.TransactionId != null ? r.TransactionId + ":fee" : null, BookingText: r.Type));
            }
        }
        return result;
    }

    private static string? Purpose(PayPalRow source, PayPalRow row)
    {
        var parts = new List<string>();
        foreach (var p in new[] { source.ItemTitle, source.Subject, source.Note })
        {
            if (p != null && !parts.Contains(p, StringComparer.Ordinal)) parts.Add(p);
        }
        if (!ReferenceEquals(source, row) && source.Currency != row.Currency)
            parts.Add($"{source.Gross.ToString("0.00", CultureInfo.InvariantCulture)} {source.Currency}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>An amount with either decimal separator ("1.234,56", "1,234.56", "-12,50", "12.5"); null when empty or unreadable.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+' or '(' or ')').ToArray());
        var negative = s.Contains('-', StringComparison.Ordinal) || (s.StartsWith('(', StringComparison.Ordinal) && s.EndsWith(')', StringComparison.Ordinal));
        s = s.Trim('(', ')').Replace("-", "", StringComparison.Ordinal).Replace("+", "", StringComparison.Ordinal);
        if (s.Length == 0) return null;
        var lastDot = s.LastIndexOf('.', StringComparison.Ordinal);
        var lastComma = s.LastIndexOf(',', StringComparison.Ordinal);
        char? decimalSep = null;
        if (lastDot >= 0 && lastComma >= 0) decimalSep = lastDot > lastComma ? '.' : ',';
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var last = Math.Max(lastDot, lastComma);
            // PayPal writes two decimals; a single separator followed by exactly three digits groups thousands.
            var single = s.Count(c => c == sep) == 1;
            if (single && s.Length - last - 1 != 3) decimalSep = sep;
        }
        var digits = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (c == decimalSep) digits.Append('.');
        }
        if (!decimal.TryParse(digits.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }

    /// <summary>Whether slash dates put the day first (dd/MM/yyyy), judged over all dates of the file; US order otherwise.</summary>
    private static bool DayFirst(IEnumerable<string?> dates)
    {
        foreach (var d in dates)
        {
            var parts = d?.Split('/');
            if (parts is not { Length: 3 }) continue;
            if (int.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) && a > 12) return true;
            if (int.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b > 12) return false;
        }
        return false;
    }

    private static DateOnly? ParseDate(string? text, bool dayFirst)
    {
        if (text == null) return null;
        string[] formats = text.Contains('/', StringComparison.Ordinal)
            ? dayFirst ? ["d/M/yyyy", "d/M/yy"] : ["M/d/yyyy", "M/d/yy"]
            : ["d.M.yyyy", "d.M.yy", "yyyy-MM-dd"];
        return DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string Normalize(string header) => header.Trim().Trim('"').Trim();

    private static int Find(List<string> names, string key) =>
        Columns[key].Select(alias => names.FindIndex(n => string.Equals(n, alias, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);

    private static string FirstLine(string text)
    {
        text = text.TrimStart('﻿');
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static char Delimiter(string header)
    {
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(c => header.Count(x => x == c)).First();
    }

    private static List<string> Split(string line, char delimiter) => Records(line, delimiter).FirstOrDefault() ?? [];

    /// <summary>The file's records (RFC 4180 quoting: quoted fields may hold the delimiter, doubled quotes and line breaks).</summary>
    private static List<List<string>> Records(string text, char? delimiter = null)
    {
        var sep = delimiter ?? Delimiter(FirstLine(text));
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var step = 1;
            if (quoted)
            {
                if (c == '"' && next == '"') { field.Append('"'); step = 2; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { record.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && next == '\n') step = 2;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
            }
            else field.Append(c);
            i += step;
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}
