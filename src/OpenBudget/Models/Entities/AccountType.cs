using System.Linq.Expressions;

namespace OpenBudget.Models;

/// <summary>The kind of an account, as stored in <see cref="Account.Type"/>.</summary>
public enum AccountType
{
    Other = 0,
    Giro = 1,
    Savings = 2,
    CreditCard = 3,
    Loan = 4,
    Manual = 5,
    Cash = 6,
    /// <summary>A securities account; see also <see cref="Account.Portfolio"/>.</summary>
    Depot = 7,
    /// <summary>A group entry of the source app's sidebar, not a real account.</summary>
    Group = 8,
}

/// <summary>What an account is used for beyond its <see cref="AccountType"/>, as stored in <see cref="Account.Role"/>.</summary>
public enum AccountRole
{
    None = 0,
    /// <summary>The balance of a payment service: topped up from other own accounts, pays for purchases.</summary>
    PaymentWallet = 1,
    /// <summary>The cash side of a broker: money moves in and out for buying and selling securities.</summary>
    BrokerCash = 2,
}

/// <summary>Values of <see cref="Category.Type"/>.</summary>
public static class CategoryTypes
{
    /// <summary>A category for money going out.</summary>
    public const long Expense = 1;

    /// <summary>A category for money coming in.</summary>
    public const long Income = 2;
}

/// <summary>Account rules that translate to SQL.</summary>
public static class AccountRules
{
    /// <summary>A depot: of type <see cref="AccountType.Depot"/>, or marked as aggregating portfolio positions.</summary>
    public static readonly Expression<Func<Account, bool>> IsDepot =
        a => a.Type == (long)AccountType.Depot || a.Portfolio == 1;

    private static readonly Func<Account, bool> IsDepotCompiled = IsDepot.Compile();

    /// <summary>Only the depots (see <see cref="IsDepot"/>).</summary>
    public static IQueryable<Account> WhereDepot(this IQueryable<Account> accounts) => accounts.Where(IsDepot);

    /// <summary>Only the accounts that are not depots (see <see cref="IsDepot"/>).</summary>
    public static IQueryable<Account> WhereNotDepot(this IQueryable<Account> accounts) =>
        accounts.Where(Expression.Lambda<Func<Account, bool>>(Expression.Not(IsDepot.Body), IsDepot.Parameters));

    /// <summary>Whether an account in memory is a depot (see <see cref="IsDepot"/>).</summary>
    public static bool IsDepotAccount(Account a) => IsDepotCompiled(a);
}
