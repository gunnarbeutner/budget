namespace OpenBudget.Models;

/// <summary>
/// One entry of the virtualized transaction list: the row, its position in the filtered list, and - in single-account
/// views - whether a day separator precedes it and what the balance was at the end of that day.
/// </summary>
public sealed record TxListItem(TransactionRow Row, int Index, bool ShowDay, DateOnly Day, decimal? DayBalance)
{
    /// <summary>Identifies list items by their transaction, so a refreshed list keeps the rows it already shows.</summary>
    public static IEqualityComparer<TxListItem> ById { get; } = EqualityComparer<TxListItem>.Create((a, b) => a?.Row.Id == b?.Row.Id, i => i.Row.Id.GetHashCode());
}
