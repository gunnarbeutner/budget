using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBudget.Connectors.PayPal;

namespace OpenBudget.Connectors.Amazon;

/// <summary>One page of the wallet's transaction list, and how to ask for the next one.</summary>
internal sealed record AmazonWalletPage(IReadOnlyList<RemoteCharge> Charges, bool HasMore, string? NextPageKey, int Entries)
{
    /// <summary>The oldest day on the page (null: an empty page).</summary>
    public DateOnly? Oldest { get; init; }

    /// <summary>The newest day on the page.</summary>
    public DateOnly? Newest { get; init; }
}

/// <summary>
/// Reads amazon.de's pages. Everything that depends on the site's markup lives here.
///
/// Wallet (cpe/yourpayments/transactions): a Next.js page whose first page of transactions is in the
/// <c>__NEXT_DATA__</c> script at <c>props.pageProps.state.transactionResponseState.visibleTransactionResponse</c>
/// (<c>transactionList</c>, 20 per page; <c>hasMore</c> and <c>lastEvaluatedPageKey</c> for paging). Further pages
/// answer in the same shape. Each entry has <c>formattedAmount</c> ("-€10.60" / "-10,60 €"), <c>formattedDate</c>
/// ("12 Nov 2026", "26 Sept 2026", "12. Nov. 2026"), <c>statementDescriptor</c>, <c>statusInfo</c>, <c>orderData</c>
/// (a list, or an object keyed "0", "1", … listing every order one payment covered) with <c>orderDisplayString</c> ("Order #304-…") and <c>orderDetailsUrl</c>, and
/// <c>paymentMethodDisplayStringData</c> (<c>paymentMethodName</c>, <c>paymentMethodNumber.lastDigits</c>; points
/// have no number). Entries without an order are skipped.
///
/// Order details (gp/your-account/order-details?orderID=…): server HTML with <c>data-component</c> markers:
/// <c>orderDate</c>, <c>chargeSummary</c> (with "Grand Total" / "Gesamtsumme"), and per item <c>itemTitle</c> (a link
/// whose href holds the ASIN), <c>quantity</c> and <c>unitPrice</c>; an item belongs to the shipment whose
/// <c>shipmentStatus</c> comes last before it.
/// </summary>
internal static partial class AmazonPages
{
    /// <summary>Payment methods without a booking behind them.</summary>
    private static readonly string[] SkipWords = ["points", "punkte", "gift card", "geschenkkarte", "gutschein", "reward", "prämie"];

    /// <summary>Statuses of entries that did not move money.</summary>
    private static readonly string[] FailedWords = ["declin", "fail", "cancel", "abgelehnt", "fehlgeschlagen", "storniert"];

    private static readonly string[] TotalLabels = ["Grand Total", "Gesamtsumme", "Gesamtbetrag", "Order Total", "Summe"];

    [GeneratedRegex("""<script[^>]*id\s*=\s*["']__NEXT_DATA__["'][^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline | RegexOptions.ExplicitCapture, 2000)]
    private static partial Regex NextData();

    [GeneratedRegex(@"(?<id>[A-Z0-9]{3}-\d{7}-\d{7})", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex OrderNumber();

    [GeneratedRegex(@"(?<d>\d{1,2})\.?\s+(?<m>[A-Za-zÄÖÜäöüé]{3,10})\.?,?\s+(?<y>\d{4})", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"(?<m>[A-Za-z]{3,10})\.?\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex MonthDayYear();

    [GeneratedRegex(@"(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"[-+−]?\s*(?:€|EUR)\s*[-+−]?\d[\d.,]*|[-+−]?\d[\d.,]*\s*(?:€|EUR)", RegexOptions.None, 1000)]
    private static partial Regex Money();

    [GeneratedRegex(@"<(?<tag>script|style)[^>]*>.*?</\k<tag>>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Spaces();

    [GeneratedRegex("""<a\b[^>]*href\s*=\s*["'](?<href>[^"']*)["'][^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"/(?:dp|gp/product)/(?<asin>[A-Z0-9]{10})", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Asin();

    [GeneratedRegex(@"\d+", RegexOptions.None, 1000)]
    private static partial Regex Number();

    // ---- sign-in detection ----

    /// <summary>Whether a page is Amazon's sign-in, second factor or captcha instead of the page asked for.</summary>
    public static bool IsSignInPage(Uri? url, string html)
    {
        var path = url?.AbsolutePath ?? "";
        if (path.StartsWith("/ap/", StringComparison.OrdinalIgnoreCase) || path.Contains("validateCaptcha", StringComparison.OrdinalIgnoreCase)) return true;
        return html.Contains("id=\"ap_email\"", StringComparison.Ordinal) || html.Contains("id=\"ap_password\"", StringComparison.Ordinal)
            || html.Contains("captchacharacters", StringComparison.Ordinal) || html.Contains("/errors/validateCaptcha", StringComparison.Ordinal);
    }

    // ---- wallet ----

    /// <summary>The <c>__NEXT_DATA__</c> JSON of a page, or null.</summary>
    public static JsonNode? NextDataOf(string html)
    {
        var m = NextData().Match(html);
        if (!m.Success) return null;
        try { return JsonNode.Parse(WebUtility.HtmlDecode(m.Groups["json"].Value)); }
        catch (JsonException) { return null; }
    }

    /// <summary>The wallet's first page (from its <c>__NEXT_DATA__</c>); null when the page holds no transaction list.</summary>
    public static AmazonWalletPage? ParseWalletHtml(string html)
    {
        var data = NextDataOf(html);
        return data == null ? null : ParseWalletJson(data);
    }

    /// <summary>
    /// What the wallet sends with its own requests for further pages: the page token (header <c>x-amzn-upx-token</c>),
    /// locale, surface and widget name from <c>props.pageProps</c>. Null when the page holds no token.
    /// </summary>
    public static AmazonWalletRequest? WalletRequestContext(string html)
    {
        var props = NextDataOf(html)?["props"]?["pageProps"];
        if (Str(props?["token"]) is not { Length: > 0 } token) return null;
        var context = props?["manageWalletRequest"]?["requestContext"];
        var surface = context?["surfaceInfo"] is JsonObject si
            ? new JsonObject
            {
                ["surfaceType"] = si["surfaceType"]?.DeepClone(),
                ["clientApplicationType"] = si["clientApplicationType"]?.DeepClone(),
                ["surfaceFeatures"] = new JsonArray(),
                ["userAgent"] = si["userAgent"]?.DeepClone(),
            }
            : null;
        return new AmazonWalletRequest(token, Str(context?["localeInfo"]?["locale"]) ?? "de_DE", surface,
            Str(props?["request"]?["widgetName"]) ?? "ViewTransactions");
    }

    /// <summary>The body of a request for the wallet page after <paramref name="pageKey"/>, shaped like the page's own.</summary>
    public static JsonObject WalletPageBody(AmazonWalletRequest request, string pageKey) => new()
    {
        ["type"] = "GetTransactions",
        ["locale"] = request.Locale,
        ["surfaceInfo"] = request.SurfaceInfo?.DeepClone(),
        ["requestTraceId"] = TraceId(20),
        ["applicationInstanceTraceId"] = TraceId(33),
        ["transactionsViewRequest"] = new JsonObject { ["filtersControls"] = new JsonObject { ["includeFilters"] = true } },
        ["exclusiveStartKey"] = pageKey,
        ["widgetName"] = request.WidgetName,
    };

    private static string TraceId(int length) => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(length))[..length];

    /// <summary>A transaction list wherever it sits in <paramref name="root"/> (the first page or a further page's answer).</summary>
    public static AmazonWalletPage? ParseWalletJson(JsonNode root)
    {
        if ((Find(root, "transactionList") ?? Find(root, "transactionsList")) is not JsonArray list) return null;
        var charges = new List<RemoteCharge>();
        DateOnly? oldest = null, newest = null;
        foreach (var entry in list.OfType<JsonObject>())
        {
            var date = ParseDate(Str(entry["formattedDate"]));
            if (date is { } d && (oldest == null || d < oldest)) oldest = d;
            if (date is { } e && (newest == null || e > newest)) newest = e;
            if (ParseCharge(entry, date) is { } charge) charges.Add(charge);
        }
        var key = (Find(root, "lastEvaluatedPageKey") ?? Find(root, "lastEvaluatedKey")) switch
        {
            JsonValue s when s.TryGetValue<string>(out var str) => str,
            JsonObject o => o.ToJsonString(),
            _ => null,
        };
        // The first page says hasMore; further pages only carry a key while older payments follow.
        var hasMore = Find(root, "hasMore") is not JsonValue v || !v.TryGetValue<bool>(out var more) || more;
        return new AmazonWalletPage(charges, hasMore && !string.IsNullOrEmpty(key), key, list.Count) { Oldest = oldest, Newest = newest };
    }

    private static RemoteCharge? ParseCharge(JsonObject entry, DateOnly? date)
    {
        if (date == null) return null;
        var formatted = Str(entry["formattedAmount"]);
        var amount = ParseAmount(formatted);
        if (amount is null or 0) return null;
        var status = Str(entry["statusInfo"]?["label"]) ?? Str(entry["statusInfo"]?["status"]);
        var statusAll = Str(entry["statusInfo"]?["status"]) + " " + Str(entry["statusInfo"]?["label"]);
        if (FailedWords.Any(w => statusAll.Contains(w, StringComparison.OrdinalIgnoreCase))) return null;
        // orderData is a list, or an object keyed "0", "1", … when one payment covered several orders.
        var ids = new List<string>();
        var orders = entry["orderData"] switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => o.OrderBy(kv => int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
                .Select(kv => kv.Value).OfType<JsonObject>().ToList(),
            _ => [],
        };
        foreach (var o in orders)
        {
            var m = OrderNumber().Match(Str(o["orderDisplayString"]) ?? "");
            if (!m.Success) m = OrderNumber().Match(Str(o["orderDetailsUrl"]) ?? "");
            if (m.Success && !ids.Contains(m.Groups["id"].Value)) ids.Add(m.Groups["id"].Value);
        }
        if (ids.Count == 0) return null;
        var orderId = ids[0];
        var method = entry["paymentMethodDisplayStringData"];
        var name = (Str(method?["paymentMethodName"]) ?? "Unknown").Trim();
        var digits = Str(method?["paymentMethodNumber"]?["lastDigits"])?.Trim();
        var label = string.IsNullOrEmpty(digits) ? name : $"{name} •••• {digits}";
        var skip = string.IsNullOrEmpty(digits) && SkipWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        return new RemoteCharge(orderId, date.Value, amount.Value, CurrencyOf(formatted), InstrumentId(name, digits), label, status,
            Str(entry["statementDescriptor"]) is { Length: > 0 } descriptor ? descriptor : null)
        { Skip = skip, OtherOrderIds = ids.Skip(1).ToList() };
    }

    /// <summary>A stable id for a payment method: its name in lower case letters and digits, and the last digits.</summary>
    public static string InstrumentId(string name, string? lastDigits)
    {
        var slug = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var id = slug.ToString().Trim('-');
        return string.IsNullOrEmpty(lastDigits) ? id : id + "-" + lastDigits;
    }

    /// <summary>Whether an instrument label stands for points or a gift card (never mapped to an account).</summary>
    public static bool IsPointsLabel(string label) =>
        !label.Contains('•', StringComparison.Ordinal) && SkipWords.Any(w => label.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Find(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.TryGetPropertyValue(name, out var hit) && hit != null) return hit;
                foreach (var (_, child) in o)
                    if (Find(child, name) is { } found) return found;
                return null;
            case JsonArray a:
                foreach (var child in a)
                    if (Find(child, name) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- order details ----

    /// <summary>The order's details; items may be empty (digital orders use other pages).</summary>
    public static RemoteOrder ParseOrderDetails(string orderId, string html)
    {
        html = ScriptOrStyle().Replace(html, " ");
        var date = ParseDate(TextAfter(html, "orderDate", 400));
        decimal? total = null;
        var currency = "EUR";
        var summary = Segment(html, "chargeSummary", 6000);
        if (summary != null)
        {
            var text = Text(summary);
            foreach (var label in TotalLabels)
            {
                var at = text.LastIndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var m = Money().Match(text, at);
                if (m.Success)
                {
                    total = ParseAmount(m.Value) is { } t ? Math.Abs(t) : null;
                    currency = CurrencyOf(m.Value);
                    break;
                }
            }
        }
        var items = new List<RemoteOrderItem>();
        var starts = Markers(html, "itemTitle");
        var shipments = Markers(html, "shipmentStatus");
        var images = Markers(html, "itemImage");
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : Math.Min(html.Length, starts[i] + 8000);
            var block = html[starts[i]..end];
            var anchor = Anchor().Match(block);
            var title = anchor.Success ? Text(anchor.Groups["text"].Value) : Text(block[..Math.Min(block.Length, 600)]);
            if (string.IsNullOrWhiteSpace(title)) continue;
            var asin = anchor.Success && Asin().Match(anchor.Groups["href"].Value) is { Success: true } a ? a.Groups["asin"].Value : null;
            var quantity = Quantity(html, images, starts, i, block);
            decimal? price = TextAfter(block, "unitPrice", 300) is { } p && Money().Match(p) is { Success: true } pm ? ParseAmount(pm.Value) : null;
            var shipment = Math.Max(0, shipments.Count(at => at < starts[i]) - 1);
            items.Add(new RemoteOrderItem(Truncate(title, 500), quantity, price is { } pr ? Math.Abs(pr) : null, asin, shipment));
        }
        return new RemoteOrder(orderId, date, total, currency, items);
    }

    /// <summary>
    /// An item's quantity: the badge on its image (<c>od-item-view-qty</c>, shown only above one; it sits between the
    /// item's image and its title, so it is looked for from this item's image to the next one's), else the number inside
    /// its own <c>quantity</c> element (never text after it), else 1.
    /// </summary>
    private static int Quantity(string html, List<int> images, List<int> titles, int i, string block)
    {
        if (images.Count == titles.Count)
        {
            var from = images[i];
            var to = i + 1 < images.Count ? images[i + 1] : Math.Min(html.Length, titles[i] + 8000);
            if (QuantityBadge().Match(html[from..to]) is { Success: true } badge && Count(badge.Groups["n"].Value) is { } b) return b;
        }
        if (QuantityElement().Match(block) is { Success: true } own && Number().Match(Text(own.Groups["inner"].Value)) is { Success: true } n
            && Count(n.Value) is { } q)
            return q;
        return 1;
    }

    private static int? Count(string text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 1000 ? n : null;

    [GeneratedRegex(@"class=""[^""]*\bod-item-view-qty\b[^""]*""[^>]*>\s*(?:<span\b[^>]*>\s*)?(?<n>\d+)\s*<", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex QuantityBadge();

    [GeneratedRegex(@"data-component=""quantity""[^>]*>(?<inner>.*?)</div>", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Singleline, 1000)]
    private static partial Regex QuantityElement();

    private static List<int> Markers(string html, string component)
    {
        var list = new List<int>();
        var needle = $"data-component=\"{component}\"";
        for (var at = html.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = html.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            list.Add(at);
        return list;
    }

    private static string? Segment(string html, string component, int length)
    {
        var at = html.IndexOf($"data-component=\"{component}\"", StringComparison.Ordinal);
        return at < 0 ? null : html.Substring(at, Math.Min(length, html.Length - at));
    }

    /// <summary>The visible text right after a marker (the marker's own tag left out).</summary>
    private static string? TextAfter(string html, string component, int length)
    {
        var segment = Segment(html, component, length + 200);
        if (segment == null) return null;
        var close = segment.IndexOf('>', StringComparison.Ordinal);
        return Text(close < 0 ? segment : segment[(close + 1)..]);
    }

    private static string Text(string html) => Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---- values ----

    /// <summary>A shown amount ("-€10.60", "−10,60 €", "+€5.00") as a signed decimal.</summary>
    public static decimal? ParseAmount(string? text) =>
        text == null ? null : PayPalCsv.ParseAmount(text.Replace('−', '-').Replace("EUR", "", StringComparison.Ordinal));

    private static string CurrencyOf(string? text) =>
        text == null ? "EUR" : text.Contains('$', StringComparison.Ordinal) ? "USD" : text.Contains('£', StringComparison.Ordinal) ? "GBP" : "EUR";

    /// <summary>A shown date in English or German ("12 Nov 2026", "26 Sept 2026", "12. November 2026", "Nov 12, 2026", "12.11.2026").</summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DayMonthYear().Match(text);
        if (m.Success && Month(m.Groups["m"].Value) is { } month1) return Make(m.Groups["y"].Value, month1, m.Groups["d"].Value);
        m = MonthDayYear().Match(text);
        if (m.Success && Month(m.Groups["m"].Value) is { } month2) return Make(m.Groups["y"].Value, month2, m.Groups["d"].Value);
        m = NumericDate().Match(text);
        if (m.Success && int.TryParse(m.Groups["m"].Value, CultureInfo.InvariantCulture, out var month3)) return Make(m.Groups["y"].Value, month3, m.Groups["d"].Value);
        return null;
    }

    private static DateOnly? Make(string year, int month, string day)
    {
        var y = int.Parse(year, CultureInfo.InvariantCulture);
        var d = int.Parse(day, CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, month) ? new DateOnly(y, month, d) : null;
    }

    private static int? Month(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Length < 3) return null;
        return n[..3] switch
        {
            "JAN" or "JÄN" => 1,
            "FEB" => 2,
            "MAR" or "MÄR" or "MRZ" => 3,
            "APR" => 4,
            "MAY" or "MAI" => 5,
            "JUN" => 6,
            "JUL" => 7,
            "AUG" => 8,
            "SEP" => 9,
            "OCT" or "OKT" => 10,
            "NOV" => 11,
            "DEC" or "DEZ" => 12,
            _ => null,
        };
    }
}

/// <summary>What a request for a further wallet page carries besides the page key (see <see cref="AmazonPages.WalletRequestContext"/>).</summary>
public sealed record AmazonWalletRequest(string Token, string Locale, JsonObject? SurfaceInfo, string WidgetName);
