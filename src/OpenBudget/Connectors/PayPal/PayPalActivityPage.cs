using System.Net;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.PayPal;

/// <summary>The transactions listed on PayPal's activity page: their ids, and which of them are pending.</summary>
internal sealed record PayPalActivityList(IReadOnlyList<string> Ids, IReadOnlySet<string> PendingIds);

/// <summary>
/// Reads PayPal's activity page (/myaccount/activities), which the site renders on the server. Each transaction is a
/// <c>[data-testid=trans_overview_root_container]</c> row below an element whose <c>aria-controls</c> is
/// <c>transactionDetails-&lt;transaction id&gt;</c>; the row's <c>[data-testid=transaction_description]</c> holds the
/// day and the type and status words. A page whose rows do not all carry an id and an amount counts as unreadable.
/// </summary>
internal static partial class PayPalActivityPage
{
    private static readonly string[] PendingWords = ["pending", "ausstehend", "unclaimed", "nicht abgeholt", "in bearbeitung"];

    [GeneratedRegex("""aria-controls\s*=\s*["']transactionDetails-(?<id>[A-Za-z0-9]{6,30})["']""", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex DetailsId();

    [GeneratedRegex("""data-testid\s*=\s*["']trans_overview_root_container["']""", RegexOptions.None, 1000)]
    private static partial Regex RowMarker();

    [GeneratedRegex("""data-testid\s*=\s*["']transaction_amount["']""", RegexOptions.None, 1000)]
    private static partial Regex AmountMarker();

    [GeneratedRegex("""data-testid\s*=\s*["']transaction_description["'][^>]*>(?<text>.*?)(?=data-testid\s*=|$)""", RegexOptions.ExplicitCapture | RegexOptions.Singleline, 1000)]
    private static partial Regex Description();

    [GeneratedRegex("<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex Tag();

    /// <summary>The listed transactions; null when the page does not look like the activity list.</summary>
    public static PayPalActivityList? Parse(string html)
    {
        var rows = RowMarker().Count(html);
        var anchors = DetailsId().Matches(html);
        if (anchors.Count != rows) return null;
        var ids = new List<string>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < anchors.Count; i++)
        {
            var start = anchors[i].Index;
            var end = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            var segment = html[start..end];
            if (RowMarker().Count(segment) != 1 || !AmountMarker().IsMatch(segment)) return null;
            var description = Description().Match(segment);
            if (!description.Success) return null;
            var id = anchors[i].Groups["id"].Value;
            if (ids.Contains(id, StringComparer.Ordinal)) continue;
            ids.Add(id);
            var text = WebUtility.HtmlDecode(Tag().Replace(description.Groups["text"].Value, " "));
            if (PendingWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) pending.Add(id);
        }
        return new PayPalActivityList(ids, pending);
    }
}
