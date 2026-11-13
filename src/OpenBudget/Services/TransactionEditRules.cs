using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Why a transaction cannot be edited (or <see cref="None"/>).</summary>
public enum EditBlock
{
    None,

    /// <summary>The account is still owned by MoneyMoney: its categorization lives there until the account switches to a bank connector.</summary>
    MoneyMoney,

    /// <summary>The user may only view it (project viewer, or not signed in).</summary>
    NoPermission,
}

/// <summary>
/// Who may edit what on a transaction. OpenBudget only edits the user-owned fields (category, note, check mark, new marker);
/// the bank's own fields (amount, date, payee, purpose, ...) are never editable. Transactions of accounts still owned by
/// MoneyMoney (<see cref="AccountProviders.MoneyMoney"/>) are read-only because the next upload overwrites them.
/// </summary>
public static class TransactionEditRules
{
    /// <summary>The columns an edit may change; anything else on a transaction is the bank's or the import's.</summary>
    public static readonly IReadOnlySet<string> EditableProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Transaction.CategoryId),
        nameof(Transaction.CategorySetBy),
        nameof(Transaction.RuleId),
        nameof(Transaction.Note),
        nameof(Transaction.Checked),
        nameof(Transaction.IsNew),
        // Splits (SplitService keeps them consistent).
        nameof(Transaction.Splitted),
        nameof(Transaction.SplitOriginalAmount),
    };

    /// <summary>
    /// A split part OpenBudget creates: a child row pointing at its parent, never a bank row. Editors may add and remove
    /// these (splitting is categorisation); every other row is created and deleted only by the owner or a connector.
    /// </summary>
    public static bool IsOwnSplitPart(Transaction t) =>
        t.Splitted == SplitState.Child && t.SplitParentId != null && t.ExternalId == null;

    /// <summary>Null scope = trusted system context. <paramref name="accountProvider"/> is null for transactions without an account.</summary>
    public static EditBlock Check(AccessScope? scope, string ownerId, long? accountId, string? accountProvider)
    {
        if (accountProvider == null || string.Equals(accountProvider, AccountProviders.MoneyMoney, StringComparison.Ordinal))
            return EditBlock.MoneyMoney;
        if (scope != null && !scope.CanEditTransactionsOf(ownerId, accountId)) return EditBlock.NoPermission;
        return EditBlock.None;
    }
}
