using System.Runtime.InteropServices;
using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Filter for the per-project transaction browser. OwnerUserId is required (every project is
/// bound to exactly one owner); CategoryLeafIds / AccountIds compose with AND
/// when both are set, so a "category X on account Y" filter works. Period bounds the window;
/// null means unrestricted. AmountMin/Max compare against the absolute value of the
/// transaction amount in euros — a single number on the UI side means "transactions of at
/// least €X in magnitude," independent of sign.
/// </summary>
public class TransactionQuery
{
    public string OwnerUserId { get; init; } = string.Empty;
    public IReadOnlyCollection<long>? CategoryLeafIds { get; init; }
    public IReadOnlyCollection<long>? AccountIds { get; init; }
    /// <summary>
    /// Only uncategorized transactions (no category, or the source's empty-named sentinel).
    /// Takes precedence over <see cref="CategoryLeafIds"/>.
    /// </summary>
    public bool UncategorizedOnly { get; init; }

    /// <summary>
    /// With <see cref="CategoryLeafIds"/> set (even empty): uncategorized rows match too, limited to
    /// <see cref="UncategorizedAccountIds"/> when that is non-empty. The project's "Transactions" view.
    /// </summary>
    public bool IncludeUncategorized { get; init; }

    /// <summary>Accounts whose uncategorized rows <see cref="IncludeUncategorized"/> adds; null or empty = any account.</summary>
    public IReadOnlyCollection<long>? UncategorizedAccountIds { get; init; }
    public TimeRangeFilter? Period { get; init; }
    public string? SearchText { get; init; }
    public decimal? MinAbsAmount { get; init; }
    public decimal? MaxAbsAmount { get; init; }

    /// <summary>Only rows with (true) / without (false) the check mark; null = both.</summary>
    public bool? Checked { get; init; }

    /// <summary>Only incoming (positive) or outgoing (negative) amounts.</summary>
    public AmountDirection Direction { get; init; }

    /// <summary>Only rows still carrying the "new" marker.</summary>
    public bool NewOnly { get; init; }

    /// <summary>Booking dates from here (inclusive), UTC; null = unbounded.</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>Booking dates before this instant, UTC; null = unbounded.</summary>
    public DateTime? ToUtcExclusive { get; init; }

    /// <summary>Only rows whose payee equals this name (case-insensitive, trimmed): "show similar transactions".</summary>
    public string? PayeeEquals { get; init; }

    /// <summary>Only these rows (the live preview of a draft categorisation rule); null = no restriction.</summary>
    public IReadOnlyCollection<long>? OnlyIds { get; init; }

    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public enum AmountDirection
{
    Any,
    Incoming,
    Outgoing,
}

[StructLayout(LayoutKind.Auto)]
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

    /// <summary>The category's own color (hex) or null; see <see cref="CategoryColors"/>.</summary>
    public string? CategoryColor { get; init; }
    public long? AccountId { get; init; }
    public string? AccountName { get; init; }
    public decimal Amount { get; init; }
    public string? Currency { get; init; }
    public bool IsSplitChild { get; init; }

    /// <summary>For split parts: the booking they belong to.</summary>
    public long? SplitParentId { get; init; }

    /// <summary>Unallocated rest of a split booking whose parts don't add up to the total.</summary>
    public bool IsSplitRemainder { get; init; }

    /// <summary>True while the booking is only reserved ("vorgemerkt") and not yet booked.</summary>
    public bool IsPending { get; init; }

    public string OwnerUserId { get; init; } = string.Empty;

    /// <summary>OpenBudget's own note (MoneyMoney's comment for MoneyMoney-owned rows).</summary>
    public string? Note { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>The "unread" dot.</summary>
    public bool IsNew { get; init; }

    /// <summary>"import", "manual" or "rule"; see <see cref="TransactionCategorySource"/>.</summary>
    public string CategorySetBy { get; init; } = TransactionCategorySource.Import;

    public int? RuleId { get; init; }

    public string? AccountProvider { get; init; }

    /// <summary>Whether the current user may change category, note and check mark of this row, and if not why.</summary>
    public EditBlock EditBlock { get; init; } = EditBlock.MoneyMoney;

    public bool CanEdit => EditBlock == EditBlock.None;

    /// <summary>The whole booking (amount, date, payee, ...) may be edited or deleted: a manual account the user owns.</summary>
    public bool CanEditBooking { get; init; }

    public DateTime? ValueDate { get; init; }
    public string? CounterpartyIban { get; init; }
    public string? CounterpartyBic { get; init; }
    public string? BookingText { get; init; }
    public string? BankReference { get; init; }
    public string? EndToEndReference { get; init; }
    public string? MandateReference { get; init; }
    public string? CreditorId { get; init; }
    public string? TransactionCode { get; init; }
    public string? Primanota { get; init; }
    public string? ReturnReason { get; init; }
}
