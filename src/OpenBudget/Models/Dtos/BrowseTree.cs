using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Pre-decorated tree for the per-project browser's left rail. Accounts on top (the project's
/// linked accounts), then the tracked-category hierarchy with spent / budget / remaining
/// folded in at every node so the UI doesn't need to recompute anything client-side.
/// </summary>
public class BrowseTree
{
    public int ProjectId { get; init; }
    public int Year { get; init; }
    public int FromMonth { get; init; }
    public int ToMonth { get; init; }
    public List<BrowseAccountNode> Accounts { get; init; } = new();
    /// <summary>Account groups of the owners of <see cref="Accounts"/> (visible ones only); the rail prunes the empty ones.</summary>
    public List<AccountGroupInfo> AccountGroups { get; init; } = new();
    public List<BrowseCategoryNode> Categories { get; init; } = new();
    /// <summary>All-time count of the owner's uncategorized transactions (split parents excluded).</summary>
    public int UncategorizedCount { get; init; }
}

public class BrowseAccountNode
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public decimal? Balance { get; init; }
    public string? Currency { get; init; }
    /// <summary>Signed net flow over the current period (incoming − outgoing).</summary>
    public decimal PeriodSum { get; init; }
    /// <summary>
    /// True for a securities depot (imported type 7 / portfolio flag). For these,
    /// <see cref="Balance"/> reflects the holdings' market value rather than the packed cash
    /// balance blob, and selecting the account in Browse surfaces its holdings.
    /// </summary>
    public bool IsPortfolio { get; init; }
    /// <summary>
    /// True when this account is explicitly assigned to the project (via
    /// ProjectAccountAssignments). False when it only shows up because a transaction in
    /// one of the project's tracked categories happens to land on this account. The Browse
    /// rail hides non-assigned accounts behind a "show more" toggle.
    /// </summary>
    public bool IsAssigned { get; init; }
    public long? GroupId { get; init; }
    public long? Sort { get; init; }
    public long? Type { get; init; }
    public string? Iban { get; init; }
    /// <summary>Bank login name for connector-linked accounts the viewer owns; null otherwise (MoneyMoney does not import a bank name).</summary>
    public string? Institution { get; init; }
    /// <summary>Last successful update (connector link, else the account's refresh timestamp), UTC.</summary>
    public DateTime? LastUpdatedUtc { get; init; }
    /// <summary>Why the last update attempt failed (connector or source app), or null.</summary>
    public string? Error { get; init; }
    /// <summary>Sum of not-yet-booked transactions; null when there are none.</summary>
    public decimal? PendingBalance { get; init; }
    /// <summary>The bank login that feeds this account, set only for its owner (who may start "Sync now").</summary>
    public int? SyncLoginId { get; init; }
    /// <summary>The <see cref="BankLoginAccount"/> row linking this account to <see cref="SyncLoginId"/> (set with it, null when the bank no longer lists it).</summary>
    public int? SyncLoginAccountId { get; init; }
    /// <summary>The bank no longer lists this account (closed).</summary>
    public bool Closed { get; init; }

    /// <summary>MoneyMoney owns the account's transactions (they are read-only in OpenBudget).</summary>
    public bool IsMoneyMoney { get; init; }

    /// <summary>Its owner marked it closed in OpenBudget (the sidebar hides it by default).</summary>
    public bool MarkedClosed { get; init; }

    /// <summary>A manual account the viewer owns: bookings are entered, edited and deleted by hand.</summary>
    public bool CanEnterTransactions { get; init; }

    /// <summary>The icon its owner picked, or null (the type's icon shows; see <see cref="DisplayIcon"/>).</summary>
    public string? Icon { get; init; }

    /// <summary>The colour (hex) its icon is tinted in, or null.</summary>
    public string? Color { get; init; }

    /// <summary>The Bootstrap icon class to show: the picked one, else the one of its type.</summary>
    public string DisplayIcon => AccountIcons.ForAccount(Icon, Type, IsPortfolio);
}

public class BrowseCategoryNode
{
    public long Id { get; init; }
    public CategoryItemType ItemType { get; init; }
    public string Name { get; init; } = "";
    public string? Color { get; init; }
    /// <summary>True when this node is one of the project's ProjectTrackingItems.</summary>
    public bool IsTracked { get; init; }
    /// <summary>True for income-classified categories (affects sign/colors in the UI).</summary>
    public bool IsIncome { get; init; }
    /// <summary>
    /// Period sum for this subtree, in the item's natural direction: positive for income items,
    /// expense items report their amount as positive (matching the source's display convention
    /// where −€947,72 of groceries reads as €947 spent).
    /// </summary>
    public decimal Spent { get; init; }
    /// <summary>Period budget total. Null when no budget context applies to this node.</summary>
    public decimal? Budget { get; init; }
    /// <summary>Budget − Spent when both are present; otherwise null.</summary>
    public decimal? Remaining { get; init; }
    public List<BrowseCategoryNode> Children { get; init; } = new();
}
