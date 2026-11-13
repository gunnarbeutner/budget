using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// How a PortfolioPerformance portfolio of a source appears in OpenBudget: whether it is shown as accounts (a depot
/// account with its positions and value history, plus a ledger account with its transactions), which project they
/// land in, and which accounts they are. User configuration keyed by the stable PP uuid; survives uploads.
/// </summary>
[Table("pp_portfolio_settings")]
public class PpPortfolioSetting
{
    [Key]
    public int Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string PpPortfolioUuid { get; set; } = "";

    /// <summary>Shown as accounts; null = the default (off for a portfolio linked to a bank depot or retired, on otherwise).</summary>
    public bool? ShowAsAccount { get; set; }

    /// <summary>The project new accounts are assigned to; null = the owner's default project for new accounts.</summary>
    public int? ProjectId { get; set; }

    /// <summary>The depot account holding the portfolio's positions, once it exists.</summary>
    public long? DepotAccountId { get; set; }

    /// <summary>The ledger account holding the portfolio's transactions, once it exists.</summary>
    public long? LedgerAccountId { get; set; }

    /// <summary>The accounts existed before (taken over from MoneyMoney) rather than being created for the portfolio.</summary>
    public bool TakenOver { get; set; }

    /// <summary>The owner chose to create new accounts although existing ones could have been taken over.</summary>
    public bool TakeoverDeclined { get; set; }
}
