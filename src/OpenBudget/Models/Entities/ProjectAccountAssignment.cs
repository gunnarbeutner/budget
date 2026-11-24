using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Which of an assigned account's bookings belong to the project.
/// Exclusive: every booking in the account counts (dedicated rental account, Mietkautionskonto).
/// Shared: only bookings whose category is in the project's category set count
/// (main household giro that sees mixed traffic).
/// Whether the account counts in the Cashflow view is a separate setting,
/// <see cref="ProjectAccountAssignment.InCashflow"/>.
/// </summary>
public enum AccountScope
{
    Shared = 0,
    Exclusive = 1,
}

/// <summary>The settings an account starts with when it joins a project.</summary>
public static class AccountAssignmentDefaults
{
    /// <summary>Booking scope for a new assignment.</summary>
    public const AccountScope Scope = AccountScope.Shared;

    /// <summary>
    /// Whether an account counts in the Cashflow view unless its owner says otherwise: the open accounts spent from day to
    /// day (giro, credit card, cash, a payment service wallet). Savings, a broker's cash account, depots,
    /// PortfolioPerformance accounts, loans, closed accounts and other or unknown types stay outside it.
    /// </summary>
    public static bool InCashflow(Account account) =>
        !account.Closed && !IsInvestment(account) && account.Role != AccountRole.BrokerCash
        && (account.Kind is AccountType.Giro or AccountType.CreditCard or AccountType.Cash || account.Role == AccountRole.PaymentWallet);

    public static bool IsInvestment(Account account) =>
        account.Provider == AccountProviders.PortfolioPerformance || account.IsDepot;

    public static ProjectAccountAssignment For(int projectId, Account account) => new()
    {
        ProjectId = projectId,
        AccountId = account.Id,
        Scope = Scope,
        InCashflow = InCashflow(account),
    };
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

    /// <summary>
    /// Whether the account counts in the Cashflow view and in the project's cashflow balance
    /// (<see cref="ProjectBalanceSummary.Balance"/>). Accounts outside it still show in Browse
    /// and in the total balance.
    /// </summary>
    public bool InCashflow { get; set; } = true;

    /// <summary>
    /// Whether the owner set <see cref="InCashflow"/>; otherwise it follows <see cref="AccountAssignmentDefaults.InCashflow"/>
    /// and changes with the account's role.
    /// </summary>
    public bool InCashflowByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Project Project { get; set; } = null!;
}
