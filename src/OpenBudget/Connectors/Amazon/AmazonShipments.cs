namespace OpenBudget.Connectors.Amazon;

/// <summary>An order item as the shipment math sees it: its line total (unit price × quantity) in cents when known, and its shipment.</summary>
public sealed record SplitItem(long Id, string Title, int Quantity, long? LineCents, int Shipment = 0, bool Returned = false);

/// <summary>
/// The items one charge paid for. <see cref="Unclear"/>: the charge could not be tied to shipments, so these are all
/// items of the order; <see cref="Instalment"/>: one of several equal charges of the order (instalments), also all items.
/// </summary>
public sealed record ChargeItems(IReadOnlyList<SplitItem> Items, bool Unclear, bool Instalment);

/// <summary>Which items of an order a charge paid for.</summary>
public static class AmazonShipments
{
    private const int MaxShipments = 10;

    /// <summary>
    /// The items a charge paid for. Amazon charges per shipment: with one shipment or one charge, that is every item.
    /// Otherwise the charge is compared with each combination of shipments: their items' total, that total scaled to the
    /// order's grand total (postage, discounts), and either one less its share of the points paid. A single combination
    /// within a few cents gives the items; none or several leave all items, flagged unclear. Several equal charges are
    /// instalments of the whole order. A refund takes the one set of items whose prices add up to it exactly, looked for
    /// among the items the order page lists as returned first, then among all; otherwise all items.
    /// </summary>
    /// <param name="cardCharges">The signed amounts of all the order's charges paid by card or bank (not points), this one included.</param>
    /// <param name="pointsCents">What points or gift cards paid for the order (positive).</param>
    public static ChargeItems ItemsForCharge(
        IReadOnlyList<SplitItem> items, long chargeCents, IReadOnlyList<long> cardCharges, long pointsCents, long? orderTotalCents)
    {
        var shipments = items.Select(i => i.Shipment).Distinct().Order().ToList();
        var charges = cardCharges.Where(c => c < 0).ToList();
        if (chargeCents > 0)
            return new(ReturnedItems(items.Where(i => i.Returned).ToList(), chargeCents) ?? ReturnedItems(items, chargeCents) ?? items, false, false);
        if (chargeCents == 0 || charges.Count <= 1) return new(items, false, false);
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

    private const int MaxReturnItems = 20;

    /// <summary>The one set of items whose lines add up to <paramref name="refundCents"/> (to the cent), or null.</summary>
    private static List<SplitItem>? ReturnedItems(IReadOnlyList<SplitItem> items, long refundCents)
    {
        if (items.Count > MaxReturnItems || items.Any(i => i.LineCents == null)) return null;
        List<SplitItem>? found = null;
        for (var mask = 1; mask < 1 << items.Count; mask++)
        {
            long sum = 0;
            for (var i = 0; i < items.Count; i++)
                if ((mask & (1 << i)) != 0) sum += items[i].LineCents!.Value;
            if (sum != refundCents) continue;
            if (found != null) return null;
            found = items.Where((_, i) => (mask & (1 << i)) != 0).ToList();
        }
        return found;
    }
}
