namespace OpenBudget.Models;

/// <summary>The user-facing tax classification of an instrument, which fixes both the regime and the
/// §23 holding period. Stored per instrument; mapped to a <see cref="TaxAssetKind"/> + period by
/// <see cref="Services.TaxMath.Resolve"/>.</summary>
public enum InstrumentTaxKind
{
    /// <summary>Share/ETF/fund — Abgeltungsteuer with Teilfreistellung, no holding period.</summary>
    Security = 0,
    /// <summary>Crypto &amp; other movable §23 assets — tax-free after 1 year.</summary>
    MovableSection23 = 1,
    /// <summary>Real estate — §23 with a 10-year holding period.</summary>
    RealEstateSection23 = 2
}

/// <summary>How a security's gains are taxed in Germany — they follow two different regimes.</summary>
public enum TaxAssetKind
{
    /// <summary>Shares, ETFs, funds: gains are always taxable (Abgeltungsteuer), with no
    /// holding-period exemption; a fund's <see cref="Services.TaxMath"/> Teilfreistellung applies.</summary>
    Security,

    /// <summary>§23 private sale: tax-free once held past the Spekulationsfrist — one year for
    /// movable assets (crypto, gold), ten years for real estate — otherwise the gain is ordinary
    /// income (counts toward zvE). No Teilfreistellung. The period is passed per call.</summary>
    PrivateSale
}

/// <summary>The tax outcome of selling part of one FIFO lot.</summary>
public class TaxLotResult
{
    public DateOnly Acquired { get; init; }
    public decimal Shares { get; init; }
    /// <summary>Cost basis of the matched shares (the lot's cost, pro-rated).</summary>
    public decimal Cost { get; init; }
    /// <summary>Sale proceeds for the matched shares (shares × price).</summary>
    public decimal Proceeds { get; init; }
    /// <summary>Proceeds − cost, before any exemption (can be negative).</summary>
    public decimal GrossGain { get; init; }
    public int HoldingDays { get; init; }
    /// <summary>§23 asset held more than a year — the whole gain (or loss) is out of scope.</summary>
    public bool HoldingPeriodExempt { get; init; }
    /// <summary>The part of <see cref="GrossGain"/> that actually counts toward tax, after
    /// Teilfreistellung (securities) or the §23 holding-period exemption (private sales).</summary>
    public decimal TaxableGain { get; init; }
}

/// <summary>The realised tax outcome of selling a number of shares of one security, FIFO.</summary>
public class RealizedGain
{
    public decimal SharesSold { get; init; }
    public decimal Proceeds { get; init; }
    public decimal Cost { get; init; }
    public decimal GrossGain { get; init; }
    /// <summary>Sum of the lots' taxable gains — the figure that feeds the year's allowances and tax.</summary>
    public decimal TaxableGain { get; init; }
    public List<TaxLotResult> Lots { get; init; } = new();
    /// <summary>Requested shares the available lots couldn't cover (data gap or over-selling).</summary>
    public decimal UncoveredShares { get; init; }
}
