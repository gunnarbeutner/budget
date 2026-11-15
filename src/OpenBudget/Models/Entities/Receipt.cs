using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>Shops whose receipts OpenBudget reads.</summary>
public static class ReceiptShops
{
    /// <summary>REWE online-order invoices.</summary>
    public const string Rewe = "rewe";

    /// <summary>Lidl Plus receipts (read by the Lidl Plus connector).</summary>
    public const string Lidl = "lidl";

    /// <summary>REWE in-store receipts (eBon).</summary>
    public const string ReweEbon = "rewe-ebon";

    /// <summary>Amazon (amazon.de) orders: one receipt per card or bank charge of an order (read by the Amazon connector).</summary>
    public const string Amazon = "amazon";

    public static readonly IReadOnlyList<string> All = [Rewe, ReweEbon, Lidl, Amazon];
}

/// <summary>How a receipt relates to the bookings.</summary>
public enum ReceiptMatchState
{
    /// <summary>No booking found (yet); looked for again on every sync.</summary>
    Unmatched = 0,
    Matched = 1,

    /// <summary>Several bookings fit equally well; left for the owner.</summary>
    Ambiguous = 2,

    /// <summary>The owner said its booking is not in the app: no longer matched.</summary>
    Dismissed = 3,

    /// <summary>
    /// Nothing to look for: paid with points or a gift card, or (Amazon) the payment method is not mapped to an account.
    /// </summary>
    Skipped = 4,

    /// <summary>
    /// Amazon: the full price of an order paid in monthly instalments. The wallet lists it next to the instalments, which
    /// are charges of their own and match their bookings; the full price never reaches an account.
    /// </summary>
    Instalments = 5,
}

/// <summary>
/// What a receipt line is: a product, a deposit line (Pfand, Leergut, Pfandtasche), a fee (delivery) or a credit (Amazon:
/// what points or a gift card paid of the order, named after them).
/// </summary>
public enum ReceiptItemKind
{
    Product = 0,
    Deposit = 1,
    Fee = 2,
    Credit = 3,
}

/// <summary>Well-known values for <see cref="ReceiptItem.CategorySource"/>.</summary>
public static class ReceiptCategorySource
{
    /// <summary>The booking's own category (food, deposits, fees).</summary>
    public const string Default = "default";

    /// <summary>The owner's item category memory.</summary>
    public const string Memory = "memory";

    /// <summary>The built-in keyword sets and the extra keywords of the source's allowed categories.</summary>
    public const string Keyword = "keyword";

    public const string Ai = "ai";

    /// <summary>Moved by the owner.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// A shop's receipt (a supermarket receipt from the owner's mailbox or Lidl Plus, or an Amazon order's charge): its
/// lines, totals and the booking that paid it. Personal (owner-only query filter).
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

    /// <summary>
    /// Further orders the receipt's payment covered, space-separated (Amazon: one charge for several orders); their items
    /// follow the first order's (see <see cref="OrderNumbers"/>).
    /// </summary>
    [MaxLength(400)]
    public string? OtherOrderNumbers { get; set; }

    /// <summary>The receipt's orders: <see cref="OrderNumber"/>, then <see cref="OtherOrderNumbers"/>.</summary>
    public static IReadOnlyList<string> OrderNumbersOf(string? orderNumber, string? otherOrderNumbers) =>
        orderNumber == null ? [] : [orderNumber, .. (otherOrderNumbers ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>See <see cref="OrderNumbersOf"/>.</summary>
    [NotMapped]
    public IReadOnlyList<string> OrderNumbers => OrderNumbersOf(OrderNumber, OtherOrderNumbers);

    public DateTime? OrderDate { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public DateTime? InvoiceDate { get; set; }

    /// <summary>The sum of the lines ("Gesamtsumme") in cents.</summary>
    public long TotalCents { get; set; }

    /// <summary>Redeemed bonus credit or coupons in cents (zero or negative); Amazon: what the charge differs from its items (shipping, discounts, points).</summary>
    public long BonusCents { get; set; }

    /// <summary>What was charged ("Zahlungsbetrag") in cents (positive; an Amazon refund negative): <see cref="TotalCents"/> plus <see cref="BonusCents"/>.</summary>
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

    /// <summary>The owner assigned <see cref="TransactionId"/> by hand: kept while the booking exists, whatever its amount.</summary>
    public bool ManualMatch { get; set; }

    /// <summary>The login that read it (Amazon: one receipt per charge of an order).</summary>
    public int? BankLoginId { get; set; }

    /// <summary>Amazon: the charge's identity within the login (order, day, amount, payment method and occurrence).</summary>
    [MaxLength(200)]
    public string? SourceKey { get; set; }

    /// <summary>Amazon: the payment method's remote id (see <see cref="BankLoginAccount.RemoteId"/>), mapped to the account the charge is booked on.</summary>
    [MaxLength(200)]
    public string? PaymentMethodId { get; set; }

    /// <summary>Amazon: the payment method's name ("Visa ****1234", "Amazon gift card").</summary>
    [MaxLength(200)]
    public string? PaymentMethod { get; set; }

    /// <summary>Amazon: the order's grand total in cents (all its charges), when its details page shows it.</summary>
    public long? OrderTotalCents { get; set; }

    /// <summary>Amazon: when the order's details (its items) were read; null while they are not known.</summary>
    public DateTime? DetailsReadAt { get; set; }

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

    /// <summary>Amazon: the shipment of the order the item went out with (0-based, in page order).</summary>
    public int? Shipment { get; set; }

    /// <summary>Amazon credit lines: the identity of the points or gift card charge (see <see cref="ReceiptInvoice.SourceKey"/>).</summary>
    [MaxLength(200)]
    public string? SourceKey { get; set; }

    /// <summary>The category the item goes to (null: the booking's own category).</summary>
    public long? CategoryId { get; set; }

    /// <summary>How <see cref="CategoryId"/> was chosen (see <see cref="ReceiptCategorySource"/>).</summary>
    [MaxLength(20)]
    public string? CategorySource { get; set; }
}

/// <summary>
/// A project's item category memory: a normalised product name and the category its items go to. Written when the
/// owner moves a receipt item of a booking of the project to another category; consulted first for every later
/// receipt whose booking lies in the same project.
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

    /// <summary>The project the memory belongs to.</summary>
    public int ProjectId { get; set; }

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

    /// <summary>When the mailbox was last read successfully (the next read starts a little before it).</summary>
    public DateTime? MailCheckedAt { get; set; }

    /// <summary>When the last read (successful or not) ended.</summary>
    public DateTime? MailAttemptAt { get; set; }

    [MaxLength(1000)]
    public string? MailLastError { get; set; }

    /// <summary>How many receipt attachments the last mailbox read could not read.</summary>
    public int MailUnreadable { get; set; }
}

/// <summary>The receipt sources: what a project's item categories can be narrowed by (see <see cref="ProjectReceiptSource"/>).</summary>
public static class ReceiptSources
{
    /// <summary>The owner's mailbox (REWE online invoices and eBons).</summary>
    public const string Mail = "mail";

    public const string Lidl = "lidl";

    public const string Amazon = "amazon";

    /// <summary>Every source, in the order the receipts page shows them.</summary>
    public static readonly IReadOnlyList<string> All = [Mail, Lidl, Amazon];

    /// <summary>The source a shop's receipts come from.</summary>
    public static string Of(string shop) => shop switch
    {
        ReceiptShops.Lidl => Lidl,
        ReceiptShops.Rewe or ReceiptShops.ReweEbon => Mail,
        _ => shop,
    };
}

/// <summary>
/// A category a project's receipt items may be split into (everything else stays in the booking's own category), with
/// an optional description (what belongs there, also given to the AI) and extra keywords (comma-separated name
/// fragments). The list applies to receipts whose booking lies on an account of the project (see
/// <see cref="ProjectReceiptSource"/> for narrowing it per source). Personal like the receipts: kept by the project's owner.
/// </summary>
[Table("project_receipt_categories")]
public class ProjectReceiptCategory
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int ProjectId { get; set; }

    public long CategoryId { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? Keywords { get; set; }
}

/// <summary>
/// Narrows a project's receipt item categories (<see cref="ProjectReceiptCategory"/>) for one receipt source: only
/// <see cref="CategoryIds"/> of the project's list apply to that source's receipts. Without a row the whole list applies.
/// </summary>
[Table("project_receipt_sources")]
public class ProjectReceiptSource
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int ProjectId { get; set; }

    /// <summary>See <see cref="ReceiptSources"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = string.Empty;

    /// <summary>The categories that apply, comma-separated ids; empty: none of the list.</summary>
    [MaxLength(2000)]
    public string CategoryIds { get; set; } = string.Empty;
}

/// <summary>
/// The account a receipt source's purchases (Lidl Plus, the mailbox's REWE invoices and eBons) are paid from, as the
/// source's owner mapped it; a source may be mapped to several accounts (paid with different cards). A mapped source's
/// receipts match only bookings on its accounts, and its receipts without a booking show in every project with one of
/// them. Without a row the source matches any of the owner's bookings and shows where its bookings land.
/// </summary>
[Table("receipt_source_accounts")]
public class ReceiptSourceAccount
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>See <see cref="ReceiptSources"/> (not Amazon: its payment methods are mapped one by one).</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = string.Empty;

    public long AccountId { get; set; }
}

/// <summary>
/// A user's merge of two receipt products of the same shop (see <c>PriceAnalysis.ProductKey</c>): the purchases of
/// <see cref="FromKey"/> count as <see cref="ToKey"/>'s in the price history. Personal (owner-only query filter).
/// </summary>
[Table("receipt_product_merges")]
public class ReceiptProductMerge
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>See <see cref="ReceiptShops"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Shop { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string FromKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string ToKey { get; set; } = string.Empty;
}
