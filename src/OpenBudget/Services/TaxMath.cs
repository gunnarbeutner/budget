using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Pure German capital-gains math for selling a security: matches the sold shares to FIFO lots and
/// works out the taxable gain of each, honouring the two regimes — Abgeltungsteuer with
/// Teilfreistellung for shares/funds, and the §23 one-year Spekulationsfrist for private sales
/// (crypto &amp; co). Year-level allowances (Sparer-Pauschbetrag, §23-Freigrenze) and the actual tax
/// rate are applied a layer up, over the taxable gains this produces.
///
/// No DB, no rounding — callers round for display. Mirrors <see cref="RebalanceMath"/>.
/// </summary>
public static class TaxMath
{
    /// <summary>
    /// Realises a sale of <paramref name="sharesToSell"/> units at <paramref name="pricePerShare"/>
    /// on <paramref name="saleDate"/>, consuming <paramref name="lots"/> oldest-first.
    /// </summary>
    /// <param name="teilfreistellungRate">Fraction of a <see cref="TaxAssetKind.Security"/> gain that
    /// is tax-exempt (e.g. 0.30 for an equity fund). Applied symmetrically — it also shrinks a
    /// deductible loss. Ignored for private sales.</param>
    /// <param name="spekulationsfristYears">The §23 holding period after which a
    /// <see cref="TaxAssetKind.PrivateSale"/> is tax-free: 1 for movable assets (crypto, gold), 10
    /// for real estate. Ignored for securities.</param>
    public static RealizedGain RealizeSale(
        IReadOnlyList<OpenLot> lots,
        decimal sharesToSell,
        decimal pricePerShare,
        DateOnly saleDate,
        TaxAssetKind kind,
        decimal teilfreistellungRate = 0m,
        int spekulationsfristYears = 1)
    {
        var results = new List<TaxLotResult>();
        decimal remaining = sharesToSell;

        foreach (var lot in lots.OrderBy(l => l.Acquired).ThenBy(l => l.Cost))
        {
            if (remaining <= 0m) break;
            if (lot.Shares <= 0m) continue;

            decimal take = Math.Min(remaining, lot.Shares);
            decimal fraction = take / lot.Shares;
            decimal cost = lot.Cost * fraction;
            decimal proceeds = take * pricePerShare;
            decimal gross = proceeds - cost;

            int holdingDays = saleDate.DayNumber - lot.Acquired.DayNumber;
            // §23: held past the Spekulationsfrist (1 year movable, 10 years real estate) ⇒ out of
            // scope (gain and loss alike).
            bool exempt = kind == TaxAssetKind.PrivateSale && saleDate > lot.Acquired.AddYears(spekulationsfristYears);

            decimal taxable = exempt
                ? 0m
                : kind == TaxAssetKind.Security
                    ? gross * (1m - teilfreistellungRate)   // Teilfreistellung, symmetric on gains/losses
                    : gross;                                // §23 within the year: fully taxable income

            results.Add(new TaxLotResult
            {
                Acquired = lot.Acquired,
                Shares = take,
                Cost = cost,
                Proceeds = proceeds,
                GrossGain = gross,
                HoldingDays = holdingDays,
                HoldingPeriodExempt = exempt,
                TaxableGain = taxable
            });
            remaining -= take;
        }

        return new RealizedGain
        {
            SharesSold = sharesToSell - Math.Max(0m, remaining),
            Proceeds = results.Sum(r => r.Proceeds),
            Cost = results.Sum(r => r.Cost),
            GrossGain = results.Sum(r => r.GrossGain),
            TaxableGain = results.Sum(r => r.TaxableGain),
            Lots = results,
            UncoveredShares = Math.Max(0m, remaining)
        };
    }
}
