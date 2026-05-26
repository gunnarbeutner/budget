using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Per-budget view of stored vs. effective monthly amounts. A group row's effective amount is
/// the sum of all stored amounts beneath it (when any are non-zero), or its own stored amount
/// otherwise — that's the rule that lets a group act as a roll-up of its sub-cats without a
/// separate "leftover" concept.
/// </summary>
public class BudgetEffectiveAmounts
{
    /// <summary>Raw amounts as persisted, keyed by (Id, ItemType).</summary>
    public Dictionary<(long Id, CategoryItemType ItemType), decimal[]> Stored { get; init; } = new();

    /// <summary>What each row should display: group rows get replaced by descendant sum when
    /// applicable, all others equal their stored amount.</summary>
    public Dictionary<(long Id, CategoryItemType ItemType), decimal[]> Effective { get; init; } = new();

    /// <summary>Group keys whose effective amount is computed from descendants — the UI should
    /// render these group inputs read-only.</summary>
    public HashSet<(long Id, CategoryItemType ItemType)> AutoSummed { get; init; } = new();
}
