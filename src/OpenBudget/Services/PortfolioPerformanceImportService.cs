using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Imports security taxonomy data from an uploaded PortfolioPerformance ".portfolio" file into
/// <c>pp_security_taxonomy</c> for an existing source, keeps the file as the source's current one and recomputes the
/// user's portfolio accounts from it (<see cref="PortfolioAccountSync"/>). Parsing lives in
/// <see cref="PortfolioPerformanceFile"/>; this owns the replace-for-user transaction and the
/// per-user serialization (singleton, like the other importers).
///
/// The taxonomy supplements imported holdings (joined by ISIN or PP security uuid); an import for an unknown user throws <see cref="KeyNotFoundException"/>.
/// </summary>
public class PortfolioPerformanceImportService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<PortfolioPerformanceImportService> _logger;
    private readonly UploadedPortfolioFileSource? _files;
    private readonly PortfolioAccountSync? _accounts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perOwnerLocks = new(StringComparer.Ordinal);

    public PortfolioPerformanceImportService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        ChangeNotificationService notifier,
        ILogger<PortfolioPerformanceImportService> logger,
        UploadedPortfolioFileSource? files = null,
        PortfolioAccountSync? accounts = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
        _files = files;
        _accounts = accounts;
    }

    /// <summary>Imports the PortfolioPerformance file at <paramref name="portfolioFilePath"/> as data of <paramref name="ownerUserId"/> (a user id).</summary>
    public async Task<PpImportResult> ImportFromFileAsync(string ownerUserId, string portfolioFilePath, CancellationToken ct)
    {
        var sem = _perOwnerLocks.GetOrAdd(ownerUserId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await ImportInternalAsync(ownerUserId, portfolioFilePath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<PpImportResult> ImportInternalAsync(string ownerUserId, string portfolioFilePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (!await db.Users.AnyAsync(u => u.Id == ownerUserId, ct)) throw new KeyNotFoundException("Unknown user.");
        string ownerId = ownerUserId;

        // Parse outside the transaction — it's CPU/IO over the file, no DB work. InvalidDataException
        // (wrong format / not a PPPBV1 file) propagates to the caller for a 400.
        var (rows, securities, portfolios, lots) = await Task.Run(() =>
        {
            using var fs = File.OpenRead(portfolioFilePath);
            var client = PortfolioPerformanceFile.ReadClient(fs);
            var openLots = FifoLedger.BuildOpenLots(client)
                .Select(l => new PpLot
                {
                    OwnerUserId = ownerId,
                    PortfolioUuid = l.PortfolioUuid,
                    SecurityUuid = l.SecurityUuid,
                    Isin = l.Isin,
                    Acquired = l.Acquired,
                    Shares = l.Shares,
                    Cost = l.Cost,
                    Currency = l.Currency
                })
                .ToList();
            return (PortfolioPerformanceFile.Flatten(client, ownerId),
                    PortfolioPerformanceFile.ExtractSecurities(client, ownerId),
                    PortfolioPerformanceFile.ExtractPortfolios(client, ownerId),
                    openLots);
        }, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_security_taxonomy WHERE OwnerUserId = {0}", new object[] { ownerId }, ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_security WHERE OwnerUserId = {0}", new object[] { ownerId }, ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_lot WHERE OwnerUserId = {0}", new object[] { ownerId }, ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_portfolio WHERE OwnerUserId = {0}", new object[] { ownerId }, ct);
        db.SecurityTaxonomies.AddRange(rows);
        db.PpSecurities.AddRange(securities);
        db.PpPortfolios.AddRange(portfolios);
        db.PpLots.AddRange(lots);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        int distinctIsins = rows.Select(r => r.SecurityUuid).Distinct(StringComparer.Ordinal).Count();
        int distinctTaxonomies = rows.Select(r => r.TaxonomyName).Distinct(StringComparer.Ordinal).Count();

        _logger.LogInformation(
            "Imported PortfolioPerformance data of user {OwnerUserId}: {Rows} taxonomy rows, {Isins} classified securities, {Taxonomies} taxonomies, {Portfolios} depots, {Lots} open lots",
            ownerId, rows.Count, distinctIsins, distinctTaxonomies, portfolios.Count, lots.Count);

        // The Browse holdings view reads taxonomy alongside imported data, so signal the same area.
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerId));

        // The file becomes the user's current one; the portfolio accounts are recomputed from it.
        if (_files != null) await _files.StoreAsync(ownerId, portfolioFilePath, ct);
        if (_accounts != null)
        {
            try
            {
                await _accounts.SyncAsync(ownerId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Recomputing the PortfolioPerformance accounts of user {OwnerUserId} failed.", ownerId);
            }
        }

        return new PpImportResult
        {
            OwnerUserId = ownerId,
            RowsImported = rows.Count,
            SecuritiesClassified = distinctIsins,
            TaxonomiesImported = distinctTaxonomies,
            PortfoliosImported = portfolios.Count,
            LotsImported = lots.Count
        };
    }
}

public class PpImportResult
{
    public string OwnerUserId { get; set; } = string.Empty;
    public int RowsImported { get; set; }
    public int SecuritiesClassified { get; set; }
    public int TaxonomiesImported { get; set; }
    public int PortfoliosImported { get; set; }
    public int LotsImported { get; set; }
}
