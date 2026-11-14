using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.LidlPlus;

/// <summary>A receipt as the Lidl Plus list shows it.</summary>
public sealed record LidlTicketSummary(string Id, DateTime Date, decimal? Total);

/// <summary>One page of the Lidl Plus receipt list and how many receipts there are in all (when it says).</summary>
public sealed record LidlTicketPage(IReadOnlyList<LidlTicketSummary> Tickets, int? TotalCount);

/// <summary>
/// Reads the Lidl Plus ticket API's JSON. Amounts come as numbers or as German-formatted strings ("1,29", "-0,30");
/// quantities of weighed articles in kilograms ("0,456"). A ticket's lines are in <c>itemsLine</c> (each with its
/// discounts and, for bottles, a nested deposit); a ticket without them carries the printed receipt as HTML
/// (<c>htmlPrintedReceipt</c>), whose article lines have the same values as data attributes.
/// </summary>
public static partial class LidlTickets
{
    private static readonly TimeZoneInfo Berlin = FindBerlin();

    private static TimeZoneInfo FindBerlin()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    public static LidlTicketPage ParseList(JsonNode root)
    {
        var tickets = new List<LidlTicketSummary>();
        if (root["tickets"] is JsonArray list)
        {
            foreach (var t in list.OfType<JsonObject>())
            {
                var id = Text(t["id"]);
                if (string.IsNullOrEmpty(id) || ParseDate(t["date"]) is not { } date) continue;
                tickets.Add(new LidlTicketSummary(id, date, Amount(t["totalAmount"])));
            }
        }
        int? total = root["totalCount"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        return new LidlTicketPage(tickets, total);
    }

    /// <summary>A ticket's details as a <see cref="LidlReceipt"/>; null when it has no id, date or total.</summary>
    public static LidlReceipt? ParseDetail(JsonNode root, LidlTicketSummary? summary = null)
    {
        var id = Text(root["id"]) ?? summary?.Id;
        var date = ParseDate(root["date"]) ?? summary?.Date;
        var total = Amount(root["totalAmount"]) ?? summary?.Total;
        if (string.IsNullOrEmpty(id) || date == null || total == null) return null;

        var items = new List<LidlReceiptLine>();
        if (root["itemsLine"] is JsonArray { Count: > 0 } lines)
        {
            foreach (var line in lines.OfType<JsonObject>()) AddLine(items, line);
        }
        else if (Text(root["htmlPrintedReceipt"]) is { Length: > 0 } html)
        {
            items.AddRange(ParseHtml(html));
        }

        var currency = root["currency"] switch
        {
            JsonObject c => Text(c["code"]),
            JsonValue c => Text(c),
            _ => null,
        };
        return new LidlReceipt(id, date.Value, StoreName(root["store"]), total.Value, PaymentAmount(root["payments"]), currency is { Length: > 0 } ? currency : "EUR", items);
    }

    private static void AddLine(List<LidlReceiptLine> items, JsonObject line)
    {
        var name = (Text(line["description"]) ?? Text(line["name"]) ?? "").Trim();
        if (FoldQuantity(items, name)) return;
        var weighted = line["isWeight"] is JsonValue w && w.TryGetValue<bool>(out var isWeight) && isWeight;
        var quantity = Amount(line["quantity"]) ?? 1m;
        var unit = Amount(line["currentUnitPrice"]);
        var amount = Amount(line["originalAmount"]) ?? Amount(line["extendedAmount"]) ?? (unit is { } u ? Math.Round(u * quantity, 2) : 0m);
        var discount = 0m;
        if (line["discounts"] is JsonArray discounts)
            discount = discounts.OfType<JsonObject>().Sum(d => Math.Abs(Amount(d["amount"]) ?? 0m));
        var vat = Text(line["taxGroupName"]) ?? Text(line["taxGroup"]);
        var kind = amount < 0 && IsDeposit(name) ? LidlLineKind.DepositReturn : LidlLineKind.Article;
        items.Add(new LidlReceiptLine(name, quantity, weighted, unit, amount, vat, discount, kind));

        // a deposit charged with the article is a line of its own on the printed receipt
        if (line["deposit"] is JsonObject deposit && Amount(deposit["amount"]) is { } depositAmount && depositAmount != 0)
        {
            var depositName = (Text(deposit["description"]) ?? "Pfand").Trim();
            items.Add(new LidlReceiptLine(depositName.Length > 0 ? depositName : "Pfand", Amount(deposit["quantity"]) ?? 1m, false,
                Amount(deposit["unitPrice"]), depositAmount, Text(deposit["taxGroupName"]), 0m,
                depositAmount < 0 ? LidlLineKind.DepositReturn : LidlLineKind.Deposit));
        }
    }

    [GeneratedRegex(@"^\s*(?<n>[-+]?\d+)\s*[x×]\s*(?<unit>-?\d+,\d{2})\s*(€|EUR)?\s*$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex QuantityLine();

    /// <summary>
    /// A line that is only a quantity expression ("-4 x 0,25 €") under a deposit or deposit return line: the count and
    /// unit price of that line (whose amount is already the product), not a line of its own. True when folded.
    /// </summary>
    private static bool FoldQuantity(List<LidlReceiptLine> items, string text)
    {
        if (items.Count == 0 || QuantityLine().Match(text) is not { Success: true } m) return false;
        var last = items[^1];
        if (last.Kind == LidlLineKind.Article && !IsDeposit(last.Name)) return false;
        var count = Math.Abs(decimal.Parse(m.Groups["n"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        var unit = Math.Abs(Parse(m.Groups["unit"].Value) ?? 0m);
        var sign = last.Total < 0 || last.Kind == LidlLineKind.DepositReturn ? -1m : 1m;
        var total = last.Total != 0 ? last.Total : sign * count * unit;
        items[^1] = last with { Quantity = count, UnitPrice = sign * unit, Total = total };
        return true;
    }

    private static bool IsDeposit(string name) =>
        name.Contains("pfand", StringComparison.OrdinalIgnoreCase) || name.Contains("leergut", StringComparison.OrdinalIgnoreCase);

    private static string StoreName(JsonNode? store)
    {
        if (store is not JsonObject s) return "Lidl";
        var name = Text(s["name"])?.Trim();
        var locality = Text(s["locality"])?.Trim();
        if (string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(locality) ? "Lidl" : "Lidl " + locality;
        return string.IsNullOrEmpty(locality) || name.Contains(locality, StringComparison.OrdinalIgnoreCase) ? name : $"{name}, {locality}";
    }

    /// <summary>The card or app part of the payments (cash left out); null when the ticket lists no payments.</summary>
    private static decimal? PaymentAmount(JsonNode? payments)
    {
        if (payments is not JsonArray { Count: > 0 } list) return null;
        var paid = 0m;
        foreach (var p in list.OfType<JsonObject>())
        {
            var label = $"{Text(p["type"])} {Text(p["description"])}";
            if (IsCash(label)) continue;
            paid += Math.Abs(Amount(p["amount"]) ?? 0m);
        }
        return paid;
    }

    private static bool IsCash(string label) =>
        label.Contains("cash", StringComparison.OrdinalIgnoreCase) || BarWord().IsMatch(label);

    [GeneratedRegex(@"\bbar(geld)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex BarWord();

    // ---- printed receipt (HTML) ----

    [GeneratedRegex("""<span\b(?<attrs>[^>]*)>(?<text>[^<]*)</span>""", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Span();

    [GeneratedRegex(@"(?<name>[\w-]+)=""(?<value>[^""]*)""", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^purchase_list_line_(?<n>\d+)$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex LineId();

    [GeneratedRegex(@"(?<amount>-?\d[\d.]*,\d{2})-?\s*(?<vat>[A-Z])?\s*$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex TrailingAmount();

    private sealed class HtmlLine
    {
        public string Content = "";
        public readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Data = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The article lines of a printed receipt: spans with id "purchase_list_line_N" (one line may be split over several
    /// spans with the same id). An article line has <c>data-art-id</c> and ends with its amount and VAT letter; a weight
    /// line under it ("0,456 kg x 2,99 EUR/kg") has no amount of its own; a line with an amount but no article id is a
    /// discount on the article above, or a line of its own (returned deposit) when it is not marked as a discount.
    /// </summary>
    internal static List<LidlReceiptLine> ParseHtml(string html)
    {
        var lines = new SortedDictionary<int, HtmlLine>();
        foreach (Match span in Span().Matches(html))
        {
            var attrs = Attribute().Matches(span.Groups["attrs"].Value).ToDictionary(
                m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
            if (!attrs.TryGetValue("id", out var id) || LineId().Match(id) is not { Success: true } m) continue;
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (!lines.TryGetValue(n, out var line)) lines[n] = line = new HtmlLine();
            line.Content += WebUtility.HtmlDecode(span.Groups["text"].Value);
            if (attrs.TryGetValue("class", out var cls))
                foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)) line.Classes.Add(c);
            foreach (var (k, v) in attrs.Where(a => a.Key.StartsWith("data-", StringComparison.OrdinalIgnoreCase))) line.Data[k] = v;
        }

        var items = new List<LidlReceiptLine>();
        int? current = null;
        foreach (var line in lines.Values)
        {
            var text = line.Content.TrimEnd();
            if (text.Trim().Length == 0 || line.Classes.Contains("currency")) continue;
            if (FoldQuantity(items, text)) continue;
            var trailing = TrailingAmount().Match(text);
            if (line.Data.TryGetValue("data-art-id", out var artId) && artId.Length > 0)
            {
                if (!trailing.Success)
                {
                    if (current is { } i && text.Contains("kg", StringComparison.OrdinalIgnoreCase)) items[i] = items[i] with { Weighted = true };
                    continue;
                }
                var name = (line.Data.GetValueOrDefault("data-art-description") ?? text[..trailing.Index]).Trim();
                if (name.EndsWith("-" + artId, StringComparison.Ordinal)) name = name[..^(artId.Length + 1)].Trim();
                var amount = Parse(trailing.Groups["amount"].Value) ?? 0m;
                if (text.TrimEnd().EndsWith('-', StringComparison.Ordinal) && amount > 0) amount = -amount;
                var quantity = Parse(line.Data.GetValueOrDefault("data-art-quantity")) ?? 1m;
                var vat = trailing.Groups["vat"].Success ? trailing.Groups["vat"].Value : line.Data.GetValueOrDefault("data-tax-type");
                items.Add(new LidlReceiptLine(name, quantity, quantity != decimal.Truncate(quantity), Parse(line.Data.GetValueOrDefault("data-unit-price")),
                    amount, vat, 0m, amount < 0 && IsDeposit(name) ? LidlLineKind.DepositReturn : IsDeposit(name) ? LidlLineKind.Deposit : LidlLineKind.Article));
                current = items.Count - 1;
                continue;
            }
            if (!trailing.Success) continue;
            var value = Parse(trailing.Groups["amount"].Value) ?? 0m;
            var description = text[..trailing.Index].Trim();
            var isDiscount = line.Classes.Contains("discount") || line.Data.ContainsKey("data-promotion-id") || line.Classes.Count == 0;
            if (isDiscount && current is { } at && !IsDeposit(description))
            {
                items[at] = items[at] with { Discount = items[at].Discount + Math.Abs(value) };
                continue;
            }
            items.Add(new LidlReceiptLine(description, 1m, false, value, value, trailing.Groups["vat"].Success ? trailing.Groups["vat"].Value : null, 0m,
                value < 0 && IsDeposit(description) ? LidlLineKind.DepositReturn : IsDeposit(description) ? LidlLineKind.Deposit : LidlLineKind.Article));
            current = null;
        }
        return items;
    }

    // ---- values ----

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : null;

    /// <summary>A number, or a German-formatted amount ("1.234,56", "-0,30", "0,30-").</summary>
    internal static decimal? Amount(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.GetValueKind() == JsonValueKind.Number) return v.GetValue<decimal>();
        return v.TryGetValue<string>(out var s) ? Parse(s) : null;
    }

    internal static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace("€", "", StringComparison.Ordinal).Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.Ordinal);
        var negative = t.StartsWith('-', StringComparison.Ordinal) || t.EndsWith('-', StringComparison.Ordinal);
        t = t.Trim('-', '+');
        if (t.Contains(',', StringComparison.Ordinal)) t = t.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }

    /// <summary>The ticket's time as Berlin wall-clock time (a time without offset is taken as such).</summary>
    internal static DateTime? ParseDate(JsonNode? node)
    {
        var text = Text(node);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hasOffset = text.EndsWith('Z', StringComparison.Ordinal) || OffsetSuffix().IsMatch(text);
        if (hasOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(withOffset, Berlin).DateTime, DateTimeKind.Unspecified);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;
    }

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$", RegexOptions.None, 1000)]
    private static partial Regex OffsetSuffix();
}
