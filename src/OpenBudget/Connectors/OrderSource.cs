namespace OpenBudget.Connectors;

/// <summary>A payment of an online shop's payment list: one order, one payment instrument, a signed amount (negative = charge).</summary>
public sealed record RemoteCharge(string OrderId, DateOnly Date, decimal Amount, string Currency, string InstrumentId, string Instrument, string? Status, string? Merchant)
{
    /// <summary>Paid with points or a gift card: there is no booking for it.</summary>
    public bool Skip { get; init; }
}

/// <summary>An order with its items, read from the shop's order details.</summary>
public sealed record RemoteOrder(string OrderId, DateOnly? Date, decimal? Total, string Currency, IReadOnlyList<RemoteOrderItem> Items);

/// <summary>One item of an order; <see cref="Price"/> is the unit price, <see cref="Shipment"/> the shipment it went out with (0-based).</summary>
public sealed record RemoteOrderItem(string Title, int Quantity, decimal? Price, string? Asin, int Shipment = 0);

/// <summary>
/// What an order source read: the charges from <see cref="CoveredFrom"/> on, the details of the orders it was asked
/// for, and every payment instrument seen (as accounts, mapped to existing ones by the owner).
/// </summary>
public sealed record OrderSourceResult(
    IReadOnlyList<RemoteCharge> Charges, IReadOnlyList<RemoteOrder> Orders, IReadOnlyList<RemoteAccount> Instruments, DateOnly CoveredFrom);

/// <summary>
/// A connector that enriches existing bookings instead of delivering its own (an online shop's orders). Its accounts
/// are payment instruments; the sync calls <see cref="FetchOrdersAsync"/> instead of refreshing accounts.
/// </summary>
public interface IOrderSource
{
    /// <summary>Reads the charges back to <paramref name="since"/> and the details of orders not in <paramref name="knownOrderIds"/>.</summary>
    Task<ConnectorStep<OrderSourceResult>> FetchOrdersAsync(
        DateOnly since, IReadOnlySet<string> knownOrderIds, ChallengeAnswer? answer, CancellationToken ct);
}
