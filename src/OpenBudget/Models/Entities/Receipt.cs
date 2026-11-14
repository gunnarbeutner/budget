using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>Shops whose receipts OpenBudget reads.</summary>
public static class ReceiptShops
{
    public const string Rewe = "rewe";

    /// <summary>Lidl Plus receipts (read by the Lidl Plus connector).</summary>
    public const string Lidl = "lidl";
}

/// <summary>How a receipt relates to the bookings.</summary>
public enum ReceiptMatchState
{
    /// <summary>No booking found (yet); looked for again on every sync.</summary>
    Unmatched = 0,
    Matched = 1,

    /// <summary>Several bookings fit equally well; left for the owner.</summary>
    Ambiguous = 2,
}

/// <summary>What a receipt line is: a product, a deposit line (Pfand, Leergut, Pfandtasche) or a fee (delivery).</summary>
public enum ReceiptItemKind
{
    Product = 0,
    Deposit = 1,
    Fee = 2,
}

/// <summary>Well-known values for <see cref="ReceiptItem.CategorySource"/>.</summary>
public static class ReceiptCategorySource
{
    /// <summary>The booking's own category (food, deposits, fees).</summary>
    public const string Default = "default";

    /// <summary>The owner's item category memory.</summary>
    public const string Memory = "memory";

    /// <summary>The non-food keyword list.</summary>
    public const string Keyword = "keyword";

    public const string Ai = "ai";

    /// <summary>Moved by the owner.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// A shop's invoice (a supermarket receipt) read from the owner's mailbox: its lines, totals and the booking that paid
/// it. Personal like the mailbox it came from (owner-only query filter).
/// </summary>
[Table("receipt_invoices")]
public class ReceiptInvoice
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>See <see cref="ReceiptShops"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Shop { get; set; } = ReceiptShops.Rewe;

    /// <summary>The invoice number ("Belegnummer"); one invoice per owner, shop and number.</summary>
    [Required]
    [MaxLength(40)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>The store (branch) the receipt is from, when the shop says (Lidl Plus).</summary>
    [MaxLength(200)]
    public string? Store { get; set; }

    /// <summary>The order number ("B-XXX-XXX-XXX").</summary>
    [MaxLength(40)]
    public string? OrderNumber { get; set; }

    public DateTime? OrderDate { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public DateTime? InvoiceDate { get; set; }

    /// <summary>The sum of the lines ("Gesamtsumme") in cents.</summary>
    public long TotalCents { get; set; }

    /// <summary>Redeemed bonus credit in cents (zero or negative).</summary>
    public long BonusCents { get; set; }

    /// <summary>What was charged ("Zahlungsbetrag") in cents (positive): <see cref="TotalCents"/> plus <see cref="BonusCents"/>.</summary>
    public long PaymentCents { get; set; }

    /// <summary>The mail it came with (Microsoft Graph message id).</summary>
    [MaxLength(500)]
    public string? MessageId { get; set; }

    public DateTime ReadAt { get; set; }

    public ReceiptMatchState State { get; set; }

    /// <summary>The booking that paid the invoice (a booking of the owner), when matched.</summary>
    public long? TransactionId { get; set; }

    /// <summary>
    /// The parts this receipt split the booking into ("amount:category;…") while that split is in place; null when the
    /// receipt did not split the booking. A booking whose parts no longer match it was changed by hand and is left alone.
    /// </summary>
    [MaxLength(2000)]
    public string? SplitSignature { get; set; }

    public ICollection<ReceiptItem> Items { get; set; } = new List<ReceiptItem>();
}

/// <summary>One line of a <see cref="ReceiptInvoice"/>.</summary>
[Table("receipt_items")]
public class ReceiptItem
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public long ReceiptInvoiceId { get; set; }

    public ReceiptInvoice Invoice { get; set; } = null!;

    /// <summary>The line's position on the invoice (0-based).</summary>
    public int Position { get; set; }

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The quantity as printed: a count ("2", "-6") or a weight ("958g").</summary>
    [MaxLength(20)]
    public string Quantity { get; set; } = "1";

    /// <summary>The VAT code ("A" = 19 %, "B" = 7 %, "A/B").</summary>
    [MaxLength(5)]
    public string? VatCode { get; set; }

    public long? UnitCents { get; set; }

    /// <summary>The line total in cents (negative for returned deposit).</summary>
    public long TotalCents { get; set; }

    /// <summary>Marked "*" on the invoice: no discount or bonus credit applies to it.</summary>
    public bool NoDiscount { get; set; }

    public ReceiptItemKind Kind { get; set; }

    /// <summary>The category the item goes to (null: the booking's own category).</summary>
    public long? CategoryId { get; set; }

    /// <summary>How <see cref="CategoryId"/> was chosen (see <see cref="ReceiptCategorySource"/>).</summary>
    [MaxLength(20)]
    public string? CategorySource { get; set; }
}

/// <summary>
/// The owner's item category memory: a normalised product name and the category its items go to. Written when the
/// owner moves a receipt item to another category; consulted first for every later receipt.
/// </summary>
[Table("item_category_memory")]
public class ItemCategoryMemory
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string NormalizedName { get; set; } = string.Empty;

    public long CategoryId { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>A user's receipt settings and the state of reading their mailbox.</summary>
[Table("receipt_settings")]
public class ReceiptSettings
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>Where non-food items of supermarket receipts go (null: they stay with the booking's category).</summary>
    public long? NonFoodCategoryId { get; set; }

    /// <summary>When the mailbox was last read successfully (the next read starts a little before it).</summary>
    public DateTime? MailCheckedAt { get; set; }

    /// <summary>When the last read (successful or not) ended.</summary>
    public DateTime? MailAttemptAt { get; set; }

    [MaxLength(1000)]
    public string? MailLastError { get; set; }
}
