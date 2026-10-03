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
    public List<BrowseCategoryNode> Categories { get; init; } = new();
    /// <summary>All-time count of the source's uncategorized transactions (split parents excluded).</summary>
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
    /// True for a securities depot (MoneyMoney type 7 / portfolio flag). For these,
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
    /// expense items report their amount as positive (matching MoneyMoney's display convention
    /// where −€947,72 of groceries reads as €947 spent).
    /// </summary>
    public decimal Spent { get; init; }
    /// <summary>Period budget total. Null when no budget context applies to this node.</summary>
    public decimal? Budget { get; init; }
    /// <summary>Budget − Spent when both are present; otherwise null.</summary>
    public decimal? Remaining { get; init; }
    public List<BrowseCategoryNode> Children { get; init; } = new();
}
