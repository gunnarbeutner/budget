using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Imports security taxonomy data from an uploaded PortfolioPerformance ".portfolio" file into
/// <c>pp_security_taxonomy</c> for an existing source. Parsing lives in
/// <see cref="PortfolioPerformanceFile"/>; this owns the replace-for-source transaction and the
/// per-source serialization (singleton, like <see cref="MoneyMoneyImportService"/>).
///
/// The taxonomy supplements MoneyMoney holdings (joined by ISIN), so the source must already exist
/// — a PortfolioPerformance upload for an unknown source throws <see cref="KeyNotFoundException"/>.
/// </summary>
public class PortfolioPerformanceImportService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<PortfolioPerformanceImportService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perSourceLocks = new(StringComparer.Ordinal);

    public PortfolioPerformanceImportService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        ChangeNotificationService notifier,
        ILogger<PortfolioPerformanceImportService> logger)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<PpImportResult> ImportFromFileAsync(string sourceName, string portfolioFilePath, CancellationToken ct)
    {
        if (!MoneyMoneyImportService.IsValidSlug(sourceName))
            throw new ArgumentException($"Source name '{sourceName}' is not a valid slug.", nameof(sourceName));

        var sem = _perSourceLocks.GetOrAdd(sourceName, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await ImportInternalAsync(sourceName, portfolioFilePath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<PpImportResult> ImportInternalAsync(string sourceName, string portfolioFilePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Name == sourceName, ct)
            ?? throw new KeyNotFoundException($"Unknown source '{sourceName}'. Import its MoneyMoney data before uploading PortfolioPerformance taxonomy.");
        int sourceId = source.Id;

        // Parse outside the transaction — it's CPU/IO over the file, no DB work. InvalidDataException
        // (wrong format / not a PPPBV1 file) propagates to the caller for a 400.
        var (rows, securities) = await Task.Run(() =>
        {
            using var fs = File.OpenRead(portfolioFilePath);
            var client = PortfolioPerformanceFile.ReadClient(fs);
            return (PortfolioPerformanceFile.Flatten(client, sourceId),
                    PortfolioPerformanceFile.ExtractSecurities(client, sourceId));
        }, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_security_taxonomy WHERE SourceId = {0}", new object[] { sourceId }, ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_security WHERE SourceId = {0}", new object[] { sourceId }, ct);
        db.SecurityTaxonomies.AddRange(rows);
        db.PpSecurities.AddRange(securities);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        int distinctIsins = rows.Select(r => r.Isin).Distinct(StringComparer.Ordinal).Count();
        int distinctTaxonomies = rows.Select(r => r.TaxonomyName).Distinct(StringComparer.Ordinal).Count();

        _logger.LogInformation(
            "Imported PortfolioPerformance taxonomy for source '{Source}' (id={SourceId}): {Rows} rows, {Isins} securities, {Taxonomies} taxonomies",
            sourceName, sourceId, rows.Count, distinctIsins, distinctTaxonomies);

        // The Browse holdings view reads taxonomy alongside MoneyMoney data, so signal the same area.
        _notifier.Publish(new DataChange(DataArea.MoneyMoneyData, SourceId: sourceId));

        return new PpImportResult
        {
            SourceId = sourceId,
            SourceName = sourceName,
            RowsImported = rows.Count,
            SecuritiesClassified = distinctIsins,
            TaxonomiesImported = distinctTaxonomies
        };
    }
}

public class PpImportResult
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int RowsImported { get; set; }
    public int SecuritiesClassified { get; set; }
    public int TaxonomiesImported { get; set; }
}
