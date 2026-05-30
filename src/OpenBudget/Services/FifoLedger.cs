using OpenBudget.Models;
using OpenBudget.PortfolioPerformance.Proto;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;

namespace OpenBudget.Services;

/// <summary>
/// Reconstructs open (still-held) tax lots from a PortfolioPerformance client's transaction ledger
/// by replaying each security's buys and sells in date order and consuming sold shares FIFO — the
/// method German capital-gains tax mandates. Pure (no DB) so it's unit-testable against a hand-built
/// <see cref="PClient"/>.
///
/// FIFO is tracked <b>per depot per security</b> (German tax treats the same ISIN in two depots as
/// two independent queues). A <c>SECURITY_TRANSFER</c> between the user's depots moves the oldest
/// lots from the source depot into the target depot <b>keeping their original acquisition date and
/// cost</b> — so the Spekulationsfrist survives a broker migration. Each depot queue is held in
/// acquisition-date order, so a transferred-in old lot correctly sells before a newer local one.
/// </summary>
public static class FifoLedger
{
    // PortfolioPerformance value scaling: shares carry 8 decimals, money is stored in cents.
    private const decimal ShareScale = 100_000_000m;
    private const decimal AmountScale = 100m;

    private sealed record Lot(DateOnly Acquired, decimal Shares, decimal Cost);

    public static List<OpenLot> BuildOpenLots(PClient client)
    {
        var uuidToIsin = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
            if (!string.IsNullOrEmpty(s.Uuid) && !string.IsNullOrEmpty(s.Isin))
                uuidToIsin[s.Uuid] = s.Isin;

        // Group the share-moving transactions (incl. transfers) by ISIN.
        var byIsin = new Dictionary<string, List<PTransaction>>(StringComparer.Ordinal);
        var isinCurrency = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in client.Transactions)
        {
            if (string.IsNullOrEmpty(t.Security) || t.Date is null) continue;
            if (!uuidToIsin.TryGetValue(t.Security, out var isin)) continue;
            if (!IsAddition(t.Type) && !IsRemoval(t.Type) && t.Type != PType.SecurityTransfer) continue;
            if (!byIsin.TryGetValue(isin, out var list)) byIsin[isin] = list = new List<PTransaction>();
            list.Add(t);
            if (!isinCurrency.ContainsKey(isin) && !string.IsNullOrEmpty(t.CurrencyCode))
                isinCurrency[isin] = t.CurrencyCode;
        }

        var lots = new List<OpenLot>();
        foreach (var (isin, txns) in byIsin)
        {
            // One FIFO queue per depot, each kept in acquisition-date order.
            var depots = new Dictionary<string, LinkedList<Lot>>(StringComparer.Ordinal);
            LinkedList<Lot> Depot(string uuid)
            {
                if (!depots.TryGetValue(uuid, out var q)) depots[uuid] = q = new LinkedList<Lot>();
                return q;
            }

            foreach (var t in txns.OrderBy(t => t.Date.ToDateTime()))
            {
                decimal shares = t.Shares / ShareScale;
                if (shares <= 0m) continue;
                var portfolio = t.Portfolio ?? "";

                if (IsAddition(t.Type))
                    InsertByDate(Depot(portfolio), new Lot(DateOnly.FromDateTime(t.Date.ToDateTime()), shares, t.Amount / AmountScale));
                else if (IsRemoval(t.Type))
                    Consume(Depot(portfolio), shares, target: null);
                else // SECURITY_TRANSFER: relocate the oldest lots to the target depot, dates intact.
                    Consume(Depot(portfolio), shares, target: Depot(t.OtherPortfolio ?? ""));
            }

            var currency = isinCurrency.GetValueOrDefault(isin, "EUR");
            foreach (var (uuid, queue) in depots)
                foreach (var lot in queue)
                    lots.Add(new OpenLot
                    {
                        PortfolioUuid = uuid,
                        Isin = isin,
                        Acquired = lot.Acquired,
                        Shares = lot.Shares,
                        Cost = lot.Cost,
                        Currency = currency
                    });
        }
        return lots;
    }

    /// <summary>Inserts a lot into a depot queue ordered by acquisition date (after any equal-dated
    /// lots). Buys arrive newest-first so they fall at the end; transferred-in lots slot by their
    /// original date.</summary>
    private static void InsertByDate(LinkedList<Lot> queue, Lot lot)
    {
        var node = queue.First;
        while (node is not null && node.Value.Acquired <= lot.Acquired) node = node.Next;
        if (node is null) queue.AddLast(lot);
        else queue.AddBefore(node, lot);
    }

    /// <summary>Consumes <paramref name="amount"/> shares oldest-lot-first from <paramref name="source"/>.
    /// When <paramref name="target"/> is given the consumed pieces are re-inserted there by date (a depot
    /// transfer preserving acquisition date and proportional cost); otherwise they're dropped (a sale).</summary>
    private static void Consume(LinkedList<Lot> source, decimal amount, LinkedList<Lot>? target)
    {
        decimal remaining = amount;
        while (remaining > 0m && source.First is { } node)
        {
            var lot = node.Value;
            if (lot.Shares <= remaining)
            {
                remaining -= lot.Shares;
                source.RemoveFirst();
                if (target is not null) InsertByDate(target, lot);
            }
            else
            {
                decimal movedFraction = remaining / lot.Shares;
                decimal movedCost = lot.Cost * movedFraction;
                node.Value = lot with { Shares = lot.Shares - remaining, Cost = lot.Cost - movedCost };
                if (target is not null) InsertByDate(target, new Lot(lot.Acquired, remaining, movedCost));
                remaining = 0m;
            }
        }
    }

    // Buys and inbound deliveries open a lot; sales and outbound deliveries consume them. Transfers
    // are handled separately (relocate, not drop); cash transactions never reach here.
    private static bool IsAddition(PType t) => t is PType.Purchase or PType.InboundDelivery;
    private static bool IsRemoval(PType t) => t is PType.Sale or PType.OutboundDelivery;
}
