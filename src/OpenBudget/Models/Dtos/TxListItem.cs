namespace OpenBudget.Models;

/// <summary>
/// One entry of the virtualized transaction list: the row, its position in the filtered list, and - in single-account
/// views - whether a day separator precedes it and what the balance was at the end of that day.
/// </summary>
public sealed record TxListItem(TransactionRow Row, int Index, bool ShowDay, DateOnly Day, decimal? DayBalance);
