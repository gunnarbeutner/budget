using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// One point in MoneyMoney's <c>portfolio_chart</c>: the value of a single security within a
/// depot at a point in time. <see cref="Id"/> is namespaced via <c>SourceScope.Namespace</c>
/// from the source rowid.
///
/// <see cref="SecurityId"/> intentionally has no FK to <see cref="Holding"/>: the chart spans
/// historical positions (including ones MoneyMoney has since marked deleted) that the import does
/// not carry over, so a hard constraint would dangle. It's stored as a plain namespaced id.
/// </summary>
[Table("mm_portfolio_chart")]
public class PortfolioChartPoint
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>MoneyMoney <c>portfolio_chart.account_key</c>, namespaced to <see cref="Account.Id"/>.</summary>
    [Column("account_key")]
    public long AccountId { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    /// <summary>Unix-seconds timestamp of this valuation point.</summary>
    [Column("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>Value of the security in this depot at <see cref="Timestamp"/>, in <see cref="Currency"/>.</summary>
    [Column("balance")]
    public double? Balance { get; set; }

    /// <summary>Namespaced MoneyMoney <c>securities_key</c> (no FK — see type remarks).</summary>
    [Column("securities_key")]
    public long? SecurityId { get; set; }

    // Navigation
    public Account? Account { get; set; }

    [NotMapped]
    public DateTime Date => DateTimeOffset.FromUnixTimeSeconds(Timestamp).DateTime;
}
