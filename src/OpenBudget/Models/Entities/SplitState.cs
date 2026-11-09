namespace OpenBudget.Models;

/// <summary>
/// Values of <see cref="Transaction.Splitted"/>. MoneyMoney itself only writes Normal, Parent and
/// Child; Remainder is assigned by our import for parents whose children don't cover the parent's
/// amount.
/// </summary>
public static class SplitState
{
    public const long Normal = 0;

    /// <summary>Split parent fully allocated to its children; excluded from all totals.</summary>
    public const long Parent = 1;

    /// <summary>One part of a split booking; counts as a normal row.</summary>
    public const long Child = 2;

    /// <summary>Parent with an unallocated remainder; counts as an uncategorized normal row.</summary>
    public const long Remainder = 3;
}
