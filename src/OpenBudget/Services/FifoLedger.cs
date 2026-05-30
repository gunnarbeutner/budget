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
/// Scope is source-level per security: transactions are grouped by ISIN across all of the source's
/// depots, and internal <c>SECURITY_TRANSFER</c>s are ignored (a transfer between the user's own
/// depots neither creates nor closes a lot, and FIFO order is preserved by the surviving buy rows).
/// Per-depot attribution — needed only once lots are mapped to specific MoneyMoney depots — is a
/// later refinement; this is the level the reconciliation against MoneyMoney quantities checks.
/// </summary>
public static class FifoLedger
{
    // PortfolioPerformance value scaling: shares carry 8 decimals, money is stored in cents.
    private const decimal ShareScale = 100_000_000m;
    private const decimal AmountScale = 100m;

    public static List<OpenLot> BuildOpenLots(PClient client)
    {
        var uuidToIsin = new Dictionary<string, string>(StringComparer.Ordinal);
        var isinCurrency = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
        {
            if (string.IsNullOrEmpty(s.Uuid) || string.IsNullOrEmpty(s.Isin)) continue;
            uuidToIsin[s.Uuid] = s.Isin;
        }

        // Group the share-moving transactions by ISIN.
        var byIsin = new Dictionary<string, List<PTransaction>>(StringComparer.Ordinal);
        foreach (var t in client.Transactions)
        {
            if (string.IsNullOrEmpty(t.Security) || t.Date is null) continue;
            if (!uuidToIsin.TryGetValue(t.Security, out var isin)) continue;
            if (!IsAddition(t.Type) && !IsRemoval(t.Type)) continue;
            if (!byIsin.TryGetValue(isin, out var list)) byIsin[isin] = list = new List<PTransaction>();
            list.Add(t);
            if (!isinCurrency.ContainsKey(isin) && !string.IsNullOrEmpty(t.CurrencyCode))
                isinCurrency[isin] = t.CurrencyCode;
        }

        var lots = new List<OpenLot>();
        foreach (var (isin, txns) in byIsin)
        {
            // FIFO queue of (acquired, remaining shares, remaining cost). OrderBy is stable, so
            // same-day transactions keep their file order.
            var open = new LinkedList<(DateOnly Acq, decimal Shares, decimal Cost)>();
            foreach (var t in txns.OrderBy(t => t.Date.ToDateTime()))
            {
                decimal shares = t.Shares / ShareScale;
                if (shares <= 0m) continue;

                if (IsAddition(t.Type))
                {
                    var acquired = DateOnly.FromDateTime(t.Date.ToDateTime());
                    open.AddLast((acquired, shares, t.Amount / AmountScale));
                    continue;
                }

                // Removal: consume shares oldest-lot-first, splitting the lot it lands in.
                decimal remaining = shares;
                while (remaining > 0m && open.First is { } node)
                {
                    var lot = node.Value;
                    if (lot.Shares <= remaining)
                    {
                        remaining -= lot.Shares;
                        open.RemoveFirst();
                    }
                    else
                    {
                        decimal keptFraction = (lot.Shares - remaining) / lot.Shares;
                        node.Value = (lot.Acq, lot.Shares - remaining, lot.Cost * keptFraction);
                        remaining = 0m;
                    }
                }
            }

            var currency = isinCurrency.GetValueOrDefault(isin, "EUR");
            foreach (var (acq, shares, cost) in open)
                lots.Add(new OpenLot { Isin = isin, Acquired = acq, Shares = shares, Cost = cost, Currency = currency });
        }
        return lots;
    }

    // Buys and inbound deliveries open a lot; sells and outbound deliveries consume them. Internal
    // security transfers (and every cash transaction) are deliberately excluded — see class remarks.
    private static bool IsAddition(PType t) => t is PType.Purchase or PType.InboundDelivery;
    private static bool IsRemoval(PType t) => t is PType.Sale or PType.OutboundDelivery;
}
