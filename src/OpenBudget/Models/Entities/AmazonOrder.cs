using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>An online order read by an order source (Amazon), cached so its details are fetched once.</summary>
[Table("amazon_orders")]
public class AmazonOrder
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int BankLoginId { get; set; }

    /// <summary>The shop's order number ("304-1234567-1234567").</summary>
    [Required]
    [MaxLength(40)]
    public string OrderId { get; set; } = string.Empty;

    public DateTime? OrderDate { get; set; }

    /// <summary>The order's grand total in cents (positive), when the details page shows it.</summary>
    public long? TotalCents { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>When the details page was read (null: not yet, or it could not be read).</summary>
    public DateTime? DetailsFetchedAt { get; set; }

    public ICollection<AmazonOrderItem> Items { get; set; } = new List<AmazonOrderItem>();
}

/// <summary>One line of an <see cref="AmazonOrder"/>.</summary>
[Table("amazon_order_items")]
public class AmazonOrderItem
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public long AmazonOrderId { get; set; }

    public AmazonOrder Order { get; set; } = null!;

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    /// <summary>Unit price in cents, when shown.</summary>
    public long? PriceCents { get; set; }

    /// <summary>The shipment of the order the item went out with (0-based, in page order).</summary>
    public int Shipment { get; set; }

    [MaxLength(20)]
    public string? Asin { get; set; }

    /// <summary>The category the owner gave this item through an accepted split suggestion (evidence for later items).</summary>
    public long? CategoryId { get; set; }
}

/// <summary>How a charge relates to the bookings.</summary>
public enum AmazonChargeState
{
    /// <summary>No booking found (yet).</summary>
    Unmatched = 0,
    Matched = 1,

    /// <summary>Several bookings fit; left for the owner.</summary>
    Ambiguous = 2,

    /// <summary>Paid with points or a gift card, or the instrument is not mapped: no booking to look for.</summary>
    Skipped = 3,
}

/// <summary>A charge or refund of the shop's payment list: one order, one payment instrument, one amount.</summary>
[Table("amazon_charges")]
public class AmazonCharge
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int BankLoginId { get; set; }

    /// <summary>Identity of the charge within the login (order, day, amount, instrument and occurrence).</summary>
    [Required]
    [MaxLength(200)]
    public string ChargeKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(40)]
    public string OrderId { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>Signed cents: negative for a charge, positive for a refund.</summary>
    public long AmountCents { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>The instrument's remote id (see <see cref="BankLoginAccount.RemoteId"/>).</summary>
    [MaxLength(200)]
    public string InstrumentId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Instrument { get; set; } = string.Empty;

    public AmazonChargeState State { get; set; }

    /// <summary>The booking this charge was matched to.</summary>
    public long? TransactionId { get; set; }
}
