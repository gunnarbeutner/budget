using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;

namespace OpenBudget.Connectors.LidlPlus;

/// <summary>What a Lidl receipt line is.</summary>
public enum LidlLineKind
{
    /// <summary>An article (or a service) that was bought.</summary>
    Article = 0,

    /// <summary>A deposit charged for an article's bottles or crates.</summary>
    Deposit = 1,

    /// <summary>Returned deposit (a bottle return voucher): a negative line.</summary>
    DepositReturn = 2,
}

/// <summary>
/// One line of a <see cref="LidlReceipt"/>. <see cref="Total"/> is the line amount before its discounts (negative for a
/// deposit return), <see cref="Discount"/> the sum of the discounts on it (positive), so the line costs
/// <see cref="Net"/>. <see cref="Quantity"/> is a count, or kilograms when <see cref="Weighted"/>.
/// </summary>
public sealed record LidlReceiptLine(
    string Name, decimal Quantity, bool Weighted, decimal? UnitPrice, decimal Total, string? VatCode, decimal Discount,
    LidlLineKind Kind = LidlLineKind.Article)
{
    public decimal Net => Total - Discount;
}

/// <summary>
/// A Lidl Plus receipt with its lines. <see cref="Total"/> is what the receipt came to after discounts and coupons;
/// <see cref="PaymentAmount"/> is the part paid by card or app (what reaches a bank or card account), null when the
/// receipt does not say how it was paid. <see cref="PurchasedAt"/> is the shop's local time.
/// </summary>
public sealed record LidlReceipt(
    string ReceiptId, DateTime PurchasedAt, string Store, decimal Total, decimal? PaymentAmount, string Currency, IReadOnlyList<LidlReceiptLine> Items)
{
    /// <summary>The amount a booking for this receipt has (positive): the card/app part, else the total.</summary>
    public decimal ChargedAmount => PaymentAmount ?? Total;

    /// <summary>
    /// The receipt in the shared receipt model (see <see cref="ReceiptStore"/>): each line costs its amount after its own
    /// discounts; deposits and returned deposits are deposit lines; the receipt is dated on the purchase day, and what is
    /// charged is the card/app part. Whatever the lines do not explain (coupons on the whole receipt, a part paid in
    /// cash) is the receipt's bonus, which is spread over the lines in proportion like a REWE bonus.
    /// </summary>
    public ParsedReceipt ToParsed()
    {
        var items = Items.Select(l => new ParsedReceiptItem(
            l.Name, QuantityText(l), l.VatCode, l.UnitPrice is { } u ? Cents(u) : null, Cents(l.Net), false,
            l.Kind == LidlLineKind.Article ? ReceiptItemKind.Product : ReceiptItemKind.Deposit)).ToList();
        var total = items.Sum(i => i.TotalCents);
        var payment = Cents(ChargedAmount);
        var day = DateOnly.FromDateTime(PurchasedAt);
        return new ParsedReceipt(InvoiceNumber(ReceiptId), null, day, day, day, items, total, Math.Min(0, payment - total), payment);
    }

    /// <summary>The receipt id as stored (at most 40 characters: a longer id is shortened with a hash of the whole).</summary>
    public static string InvoiceNumber(string receiptId)
    {
        if (receiptId.Length <= 40) return receiptId;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(receiptId)))[..15];
        return receiptId[..24] + "~" + hash;
    }

    private static string QuantityText(LidlReceiptLine l) => l.Weighted
        ? decimal.Round(l.Quantity * 1000m).ToString("0", CultureInfo.InvariantCulture) + "g"
        : l.Quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}

/// <summary>A receipt a receipt source read: the shop's store (branch) and the receipt in the shared model.</summary>
public sealed record SourceReceipt(string Store, ParsedReceipt Receipt);

/// <summary>
/// A connector that delivers shop receipts instead of accounts (Lidl Plus). The sync stores them with the owner's other
/// receipts, which matches each with the booking that paid it and splits that booking by the lines
/// (see <see cref="ReceiptStore"/>).
/// </summary>
public interface IReceiptSource
{
    /// <summary>The shop the receipts are stored under (see <see cref="ReceiptShops"/>).</summary>
    string Shop { get; }

    /// <summary>Reads the receipts from <paramref name="since"/> on whose stored number is not in <paramref name="knownReceiptNumbers"/>.</summary>
    Task<ConnectorStep<IReadOnlyList<SourceReceipt>>> FetchReceiptsAsync(
        DateOnly since, IReadOnlySet<string> knownReceiptNumbers, ChallengeAnswer? answer, CancellationToken ct);
}
