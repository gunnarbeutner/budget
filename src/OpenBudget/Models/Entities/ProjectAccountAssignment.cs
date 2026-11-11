using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Controls how an assigned account's transactions contribute to a project's balance.
/// Exclusive: every transaction in the account counts (dedicated rental account, Mietkautionskonto).
/// Shared: only transactions whose category is in the project's category set count
/// (main household giro that sees mixed traffic).
/// Informational: linked for the Browse tab and balance totals only — excluded from budget and
/// cashflow math (e.g. a securities depot whose buys/sells shouldn't distort the cash projection).
/// </summary>
public enum AccountScope
{
    Shared = 0,
    Exclusive = 1,
    Informational = 2
}

/// <summary>The role an account starts with when it joins a project.</summary>
public static class AccountScopeDefaults
{
    /// <summary>Depots and PortfolioPerformance accounts are informational (no cash moves there); others count fully.</summary>
    public static AccountScope For(Account account) => IsInvestment(account) ? AccountScope.Informational : default;

    public static bool IsInvestment(Account account) =>
        account.Provider == AccountProviders.PortfolioPerformance || account.Portfolio is 1 || account.Type == 7;
}

/// <summary>
/// Links a imported account to a project so cashflow / balance views can be scoped
/// to the accounts that actually fund the project.
/// </summary>
[Table("project_account_assignments")]
public class ProjectAccountAssignment
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    /// <summary>
    /// rowid from the source's accounts table. No FK — Account lives in the read-only
    /// imported data; we resolve it in code via BudgetDbContext.
    /// </summary>
    public long AccountId { get; set; }

    public AccountScope Scope { get; set; } = AccountScope.Shared;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Project Project { get; set; } = null!;
}
