namespace OpenBudget.Models;

/// <summary>
/// Filter for the per-project transaction browser. SourceId is required (every project is
/// bound to exactly one MoneyMoney source); CategoryLeafIds / AccountIds compose with AND
/// when both are set, so a "category X on account Y" filter works. Period bounds the window;
/// null means unrestricted. AmountMin/Max compare against the absolute value of the
/// transaction amount in euros — a single number on the UI side means "transactions of at
/// least €X in magnitude," independent of sign.
/// </summary>
public class TransactionQuery
{
    public int SourceId { get; init; }
    public IReadOnlyCollection<long>? CategoryLeafIds { get; init; }
    public IReadOnlyCollection<long>? AccountIds { get; init; }
    public TimeRangeFilter? Period { get; init; }
    public string? SearchText { get; init; }
    public decimal? MinAbsAmount { get; init; }
    public decimal? MaxAbsAmount { get; init; }
    public int Page { get; init; } = 0;
    public int PageSize { get; init; } = 50;
}

public readonly record struct TimeRangeFilter(int Year, int FromMonth, int ToMonth);

public class TransactionPage
{
    public IReadOnlyList<TransactionRow> Rows { get; init; } = Array.Empty<TransactionRow>();
    public int TotalCount { get; init; }
    public decimal TotalSum { get; init; }
}

public class TransactionRow
{
    public long Id { get; init; }
    public DateTime Date { get; init; }
    public string? Name { get; init; }
    public string? Comment { get; init; }
    /// <summary>
    /// SEPA Verwendungszweck / purpose — the bank's raw transaction text. Often the only
    /// place a payment's true identity lives when <see cref="Name"/> is just a payment
    /// processor like "PayPal".
    /// </summary>
    public string? Purpose { get; init; }
    public long? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public long? AccountId { get; init; }
    public string? AccountName { get; init; }
    public decimal Amount { get; init; }
    public string? Currency { get; init; }
    public bool IsSplitChild { get; init; }
}
