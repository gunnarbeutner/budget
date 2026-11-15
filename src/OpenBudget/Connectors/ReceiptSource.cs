using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;

namespace OpenBudget.Connectors;

/// <summary>A receipt a receipt source read: the shop's store (branch) and the receipt in the shared model.</summary>
public sealed record SourceReceipt(string Store, ParsedReceipt Receipt);

/// <summary>
/// A connector that delivers shop receipts instead of accounts (a shop app). The sync stores them with the owner's other
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
