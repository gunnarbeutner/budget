using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// imported transaction imported into our DB. <see cref="Id"/> encodes the source via
/// <c>SourceScope.Namespace(SourceId, rowid)</c>; <see cref="SourceId"/> is duplicated as a
/// real column for indexed filtering.
/// </summary>
[Table("transactions")]
public class Transaction
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

    [Column("local_account_key")]
    public long? AccountId { get; set; }

    /// <summary>
    /// Transaction amount in integer cents (or the currency's smallest unit). Stored as a
    /// long so DB-side SUM stays exact for typical personal-budget magnitudes — the previous
    /// REAL column accumulated sub-cent rounding error in aggregates.
    /// </summary>
    [Column("amount")]
    public long Amount { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    [Column("timestamp")]
    public long Timestamp { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    /// <summary>
    /// SEPA "Verwendungszweck" / purpose field — the bank's raw transaction text including
    /// EREF / MREF / CRED references and the original creditor's billing line. Often the
    /// only place a payment's real identity lives, since <see cref="Name"/> is sometimes
    /// just the payment processor (e.g. "PayPal").
    /// </summary>
    [Column("purpose")]
    public string? Purpose { get; set; }

    [Column("category_key")]
    public long? CategoryId { get; set; }

    [Column("checkmark")]
    public long? Checkmark { get; set; }

    /// <summary>
    /// imported split marker; see <see cref="SplitState"/>. 0 = normal row, 1 = split parent that
    /// is fully allocated to its children (a placeholder: excluded from every rollup so it does
    /// not double-count), 2 = split child, 3 = split remainder (a parent whose children do not
    /// add up to its amount; the import rewrites it into an uncategorized row carrying only the
    /// unallocated part, so it counts like a normal row).
    /// </summary>
    [Column("splitted")]
    public long? Splitted { get; set; }

    /// <summary>
    /// False while the bank has only reserved the booking ("vorgemerkt" / pending); true once it
    /// is booked. Pending rows still count toward totals like any other row. Defaults to true so
    /// rows from sources that don't report pending state read as booked.
    /// </summary>
    [Column("booked")]
    public bool Booked { get; set; } = true;

    /// <summary>
    /// For split children (<see cref="Splitted"/> == 2): the namespaced Id of the split parent
    /// row (the source's <c>split_key</c>). Null for every other row.
    /// </summary>
    [Column("split_parent_id")]
    public long? SplitParentId { get; set; }

    /// <summary>
    /// For split parents and split-remainder rows: the parent's original booking amount in
    /// cents, before the import replaced <see cref="Amount"/> with the unallocated remainder.
    /// Null for all other rows.
    /// </summary>
    [Column("split_original_amount")]
    public long? SplitOriginalAmount { get; set; }

    /// <summary>
    /// Identity of a row delivered by a bank connector (the bank's transaction id, or a stable hash of
    /// date/amount/name/purpose plus an occurrence counter when the bank gives none). Null for rows
    /// imported from MoneyMoney. Used to recognise rows again on later syncs.
    /// </summary>
    [Column("external_id")]
    public string? ExternalId { get; set; }

    /// <summary>
    /// OpenBudget's own note (<c>note</c>). For MoneyMoney-owned rows the import fills it from
    /// MoneyMoney's <see cref="Comment"/> on every upload; for connector-owned rows the user edits it.
    /// </summary>
    [Column("note")]
    public string? Note { get; set; }

    /// <summary>
    /// The check mark. MoneyMoney-owned rows mirror MoneyMoney's <see cref="Checkmark"/> on every upload;
    /// for connector-owned rows the user toggles it.
    /// </summary>
    [Column("checked")]
    public bool Checked { get; set; }

    /// <summary>"Unread" marker: set on rows a bank connector inserts, cleared once viewed or marked.</summary>
    [Column("is_new")]
    public bool IsNew { get; set; }

    /// <summary>Who chose the category; see <see cref="TransactionCategorySource"/>. Manual choices are never overwritten by rules.</summary>
    [Column("category_set_by")]
    public string CategorySetBy { get; set; } = TransactionCategorySource.Import;

    /// <summary>The rule that set the category when <see cref="CategorySetBy"/> is "rule" (rules arrive in a later step).</summary>
    [Column("rule_id")]
    public int? RuleId { get; set; }

    /// <summary>Unix seconds of the last manual categorization; feeds the "recently used" suggestions.</summary>
    [Column("category_set_at")]
    public long? CategorySetAt { get; set; }

    /// <summary>The bank's value date (unix seconds), when the connector reports one.</summary>
    [Column("value_timestamp")]
    public long? ValueTimestamp { get; set; }

    [Column("counterparty_iban")]
    public string? CounterpartyIban { get; set; }

    [Column("counterparty_bic")]
    public string? CounterpartyBic { get; set; }

    /// <summary>The bank's own booking text / transaction type ("Lastschrift", "Überweisung", ...).</summary>
    [Column("booking_text")]
    public string? BookingText { get; set; }

    /// <summary>The bank's own transaction reference, when it gives one.</summary>
    [Column("bank_reference")]
    public string? BankReference { get; set; }

    /// <summary>
    /// True when the booking moves money between the household's own accounts: it then counts as neither income nor
    /// spending (budget, cashflow, category sums) while the account balance still includes it. Set on both rows of a
    /// linked pair, or on a single row whose counterpart account is not tracked ("one-sided" transfer).
    /// </summary>
    [Column("is_transfer")]
    public bool IsTransfer { get; set; }

    /// <summary>The other booking of a linked transfer (same source, other account). Null for one-sided transfers.</summary>
    [Column("transfer_peer_id")]
    public long? TransferPeerId { get; set; }

    [NotMapped]
    public DateTime Date => DateTimeOffset.FromUnixTimeSeconds(Timestamp).DateTime;

    // Navigation properties
    public Category? Category { get; set; }
    public Account? Account { get; set; }
}

/// <summary>Well-known values for <see cref="Transaction.CategorySetBy"/>.</summary>
public static class TransactionCategorySource
{
    /// <summary>Delivered by the MoneyMoney upload (read-only in OpenBudget).</summary>
    public const string Import = "import";

    /// <summary>Chosen by a user in OpenBudget; never overwritten by rules.</summary>
    public const string Manual = "manual";

    /// <summary>Set by an OpenBudget rule (<see cref="Transaction.RuleId"/>).</summary>
    public const string Rule = "rule";
}
