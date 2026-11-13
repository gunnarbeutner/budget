using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A single portfolio holding (one security in one depot account) imported from the source's
/// <c>securities</c> table. <see cref="Id"/> is namespaced via <c>RowIds.Namespace</c> like
/// every imported row. Only live positions are imported — the source app keeps sold/closed holdings
/// with <c>deleted=1</c>, which the import filters out.
///
/// Money fields stay as REAL (euros), unlike the cents-as-long ledger used for transactions:
/// these are fluctuating market valuations, not penny-accurate bookkeeping, and crypto holdings
/// carry sub-cent quantities and prices (e.g. 3e-06 BTC) that integer cents would flatten to zero.
/// </summary>
[Table("holdings")]
public class Holding
{
    [Key]
    public long Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>Source <c>securities.account_key</c>, namespaced to <see cref="Account.Id"/>.</summary>
    [Column("account_key")]
    public long AccountId { get; set; }

    /// <summary>Security name (source <c>ident</c>), e.g. "Xtrackers MSCI USA (Acc)".</summary>
    [Column("name")]
    public string? Name { get; set; }

    [Column("isin")]
    public string? Isin { get; set; }

    /// <summary>German securities number / WKN (source <c>wpn</c>).</summary>
    [Column("wkn")]
    public string? Wkn { get; set; }

    [Column("symbol")]
    public string? Symbol { get; set; }

    /// <summary>PortfolioPerformance security uuid for holdings written by the PortfolioPerformance
    /// sync; null for imported holdings. Lets a holding without an ISIN find its taxonomy.</summary>
    [Column("pp_security_uuid")]
    public string? PpSecurityUuid { get; set; }

    /// <summary>Units held. REAL — crypto holdings are fractional to many decimals.</summary>
    [Column("quantity")]
    public double Quantity { get; set; }

    /// <summary>Current market value in <see cref="AmountCurrency"/> (source <c>amount</c>).</summary>
    [Column("amount")]
    public double? Amount { get; set; }

    [Column("amount_cur")]
    public string? AmountCurrency { get; set; }

    /// <summary>Current price per unit in <see cref="PriceCurrency"/> (source <c>price</c>).</summary>
    [Column("price")]
    public double? Price { get; set; }

    [Column("price_cur")]
    public string? PriceCurrency { get; set; }

    /// <summary>
    /// Purchase price per unit (source <c>purchase</c>). Populated for ETFs/funds but null for
    /// crypto, so <see cref="CostBasis"/> and <see cref="UnrealizedGain"/> are unavailable there.
    /// </summary>
    [Column("purchase")]
    public double? Purchase { get; set; }

    /// <summary>
    /// imported asset-class code. Observed: 5=Cash, 6=Equity/Fund, 7=Crypto. See
    /// <see cref="AssetClassLabel"/> for the mapping.
    /// </summary>
    [Column("asset_class")]
    public long? AssetClass { get; set; }

    // Navigation
    public Account? Account { get; set; }

    /// <summary>Human label for <see cref="AssetClass"/>; unknown codes fall back to "Other (n)".</summary>
    [NotMapped]
    public string AssetClassLabel => AssetClass switch
    {
        5 => "Cash",
        6 => "Equity",
        7 => "Crypto",
        null => "Unclassified",
        _ => $"Other ({AssetClass})"
    };

    /// <summary>Total acquisition cost (<see cref="Purchase"/> × <see cref="Quantity"/>), or null when no purchase price is known (e.g. crypto).</summary>
    [NotMapped]
    public double? CostBasis => Purchase.HasValue ? Purchase.Value * Quantity : null;

    /// <summary>Unrealized gain/loss in <see cref="AmountCurrency"/>, or null when cost basis or value is unavailable.</summary>
    [NotMapped]
    public double? UnrealizedGain => Amount.HasValue && CostBasis.HasValue ? Amount.Value - CostBasis.Value : null;

    /// <summary>Unrealized gain as a fraction of cost basis (0.1 = +10%), or null when not computable.</summary>
    [NotMapped]
    public double? UnrealizedGainFraction =>
        UnrealizedGain.HasValue && CostBasis is > 0 ? UnrealizedGain.Value / CostBasis.Value : null;
}
