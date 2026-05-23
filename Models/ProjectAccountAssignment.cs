using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Controls how an assigned account's transactions contribute to a project's balance.
/// Exclusive: every transaction in the account counts (dedicated rental account, Mietkautionskonto).
/// Shared: only transactions whose category is in the project's category set count
/// (main household giro that sees mixed traffic).
/// </summary>
public enum AccountScope
{
    Shared = 0,
    Exclusive = 1
}

/// <summary>
/// Links a MoneyMoney account to a project so cashflow / balance views can be scoped
/// to the accounts that actually fund the project.
/// </summary>
[Table("project_account_assignments")]
public class ProjectAccountAssignment
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    /// <summary>
    /// rowid from MoneyMoney's accounts table. No FK — Account lives in the read-only
    /// MoneyMoney DB; we resolve it in code via MoneyMoneyDbContext.
    /// </summary>
    public long AccountId { get; set; }

    public AccountScope Scope { get; set; } = AccountScope.Shared;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Project Project { get; set; } = null!;
}
