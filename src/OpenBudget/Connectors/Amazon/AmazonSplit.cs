namespace OpenBudget.Connectors.Amazon;

/// <summary>An order item as the split math sees it: its line total (unit price × quantity) in cents when known, and its shipment.</summary>
public sealed record SplitItem(long Id, string Title, int Quantity, long? LineCents, int Shipment = 0, long? CategoryId = null);

/// <summary>
/// The items one charge paid for. <see cref="Unclear"/>: the charge could not be tied to shipments, so these are all
/// items of the order; <see cref="Instalment"/>: one of several equal charges of the order (instalments), also all items.
/// </summary>
public sealed record ChargeItems(IReadOnlyList<SplitItem> Items, bool Unclear, bool Instalment);

/// <summary>Which items a charge paid for, and how a charged amount spreads over them.</summary>
public static class AmazonSplit
{
    private const int MaxShipments = 10;

    /// <summary>
    /// The items a charge paid for. Amazon charges per shipment: with one shipment or one charge, that is every item.
    /// Otherwise the charge is compared with each combination of shipments: their items' total, that total scaled to the
    /// order's grand total (postage, discounts), and either one less its share of the points paid. A single combination
    /// within a few cents gives the items; none or several leave all items, flagged unclear. Several equal charges are
    /// instalments of the whole order.
    /// </summary>
    /// <param name="cardCharges">The signed amounts of all the order's charges paid by card or bank (not points), this one included.</param>
    /// <param name="pointsCents">What points or gift cards paid for the order (positive).</param>
    public static ChargeItems ItemsForCharge(
        IReadOnlyList<SplitItem> items, long chargeCents, IReadOnlyList<long> cardCharges, long pointsCents, long? orderTotalCents)
    {
        var shipments = items.Select(i => i.Shipment).Distinct().Order().ToList();
        var charges = cardCharges.Where(c => c < 0).ToList();
        if (chargeCents >= 0 || charges.Count <= 1) return new(items, false, false);
        if (charges.Distinct().Count() == 1 && (shipments.Count == 1 || charges.Count > shipments.Count)) return new(items, false, true);
        if (shipments.Count == 1) return new(items, false, charges.Distinct().Count() == 1);
        if (shipments.Count > MaxShipments || items.Any(i => i.LineCents == null)) return new(items, true, false);

        var subtotal = items.Sum(i => i.LineCents!.Value);
        if (subtotal <= 0) return new(items, true, false);
        var target = Math.Abs(chargeCents);
        var tolerance = Math.Max(5, target / 100);
        var hits = new List<List<int>>();
        for (var mask = 1; mask < 1 << shipments.Count; mask++)
        {
            var chosen = shipments.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            var sum = items.Where(i => chosen.Contains(i.Shipment)).Sum(i => i.LineCents!.Value);
            var share = (decimal)sum / subtotal;
            var candidates = new List<decimal> { sum, sum - (share * pointsCents) };
            if (orderTotalCents is { } total)
            {
                candidates.Add(share * total);
                candidates.Add(share * (total - pointsCents));
            }
            if (candidates.Any(c => Math.Abs(c - target) <= tolerance)) hits.Add(chosen);
        }
        if (hits.Count != 1) return new(items, true, false);
        return new(items.Where(i => hits[0].Contains(i.Shipment)).ToList(), false, false);
    }

    /// <summary>
    /// Spreads <paramref name="totalCents"/> over weights (line totals) in proportion, so shipping, discounts and points
    /// fall on every item alike. Each share is rounded to cents; what rounding leaves over goes to the largest item. No
    /// usable weights: equal shares. The shares carry the total's sign and add up to it exactly.
    /// </summary>
    public static long[] Proportion(long totalCents, IReadOnlyList<long?> weights)
    {
        if (weights.Count == 0) return [];
        var abs = Math.Abs(totalCents);
        var w = weights.Select(x => x is > 0 ? x.Value : 0m).ToArray();
        if (w.Sum() == 0) w = Enumerable.Repeat(1m, weights.Count).ToArray();
        var sum = w.Sum();
        var parts = w.Select(x => (long)Math.Round(abs * x / sum, MidpointRounding.AwayFromZero)).ToArray();
        var largest = Array.IndexOf(w, w.Max());
        parts[largest] += abs - parts.Sum();
        var sign = totalCents < 0 ? -1 : 1;
        return parts.Select(p => p * sign).ToArray();
    }

    /// <summary>The items as one line of text: "USB-C charger; Laundry detergent ×2".</summary>
    public static string Describe(IEnumerable<(string Title, int Quantity)> items)
    {
        var text = string.Join("; ", items.Select(i => i.Quantity > 1 ? $"{i.Title} ×{i.Quantity}" : i.Title));
        return text.Length <= 2000 ? text : text[..2000];
    }
}
