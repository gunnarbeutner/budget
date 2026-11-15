using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Importers.MoneyMoney;

namespace OpenBudget.Services.DataFixes;

/// <summary>
/// Every data fix, in the order they run. Names are recorded in <c>data_fixes</c> and must never change; a new fix is
/// appended. Fixes repair data written before the code that now keeps it right on write.
/// </summary>
public static class DataFixList
{
    public static IReadOnlyList<DataFix> All { get; } =
    [
        new("2026-11-amazon-receipt-tables",
            "Amazon orders in the former amazon_* tables move to the shared receipts; the tables are dropped",
            async c =>
            {
                if (await AmazonReceiptMigration.RunAsync(c.Db, c.NowUtc, c.CancellationToken) is { } moved)
                    c.Logger.LogInformation(
                        "Amazon: {Charges} charges ({Matched} matched) moved to receipts; {Adopted} splits kept as the receipts', {LeftAlone} left as set by hand; {Memory} item memory entries.",
                        moved.Charges, moved.Matched, moved.SplitsAdopted, moved.SplitsLeftAlone, moved.MemoryEntries);
            },
            Transactional: false),   // opens its own transaction

        new("2026-11-amazon-points-receipts",
            "Stored Amazon receipts of single points or gift card charges become credit lines of their orders",
            c => AmazonReceiptStore.FoldPointsReceiptsAsync(c.Db, c.NowUtc, c.CancellationToken)),

        new("2026-11-portfolio-legacy-files",
            "PortfolioPerformance files stored under a former per-data-set number move to their owner's name",
            c => c.Services.GetRequiredService<UploadedPortfolioFileSource>().RelocateLegacyFilesAsync(c.Db, c.CancellationToken)),

        new("2026-11-moneymoney-rules",
            "Category rules of MoneyMoney data imported before rules were imported",
            c => MoneyMoneyRuleImport.BackfillAsync(c.Db, c.CancellationToken)),

        new("2026-11-category-colors",
            "Category groups and categories created without a colour get one",
            c => CategoryColorBackfill.FillAsync(c.Db, null, c.CancellationToken)),

        new("2026-11-zero-companions",
            "Uncategorized zero-amount bookings take their main booking's category",
            async c =>
            {
                if (await ZeroCompanions.FollowAsync(c.Db, null, null, null, c.CancellationToken) > 0) await c.Db.SaveChangesAsync(c.CancellationToken);
            }),

        new("2026-11-user-default-projects",
            "Users created before every user got a project get one",
            c => UserDefaultProject.EnsureAllAsync(c.Services.GetRequiredService<SystemDbContextFactory>(), c.CancellationToken),
            Transactional: false),   // works through its own contexts

        // Last: it waits for configured secrets, and holds back the fixes after it until then.
        new("2026-11-background-defaults",
            "Logins added before background syncs defaulted on get that default once",
            async c =>
            {
                var secrets = c.Services.GetRequiredService<SecretsProtector>();
                // Credentials are needed to tell which logins can sync on their own: without them, wait for a start that has them.
                if (!secrets.IsConfigured && await c.Db.BankLogins.IgnoreQueryFilters().AnyAsync(l => !l.BackgroundDefaulted, c.CancellationToken))
                    throw new InvalidOperationException("Secrets are not configured; logins without a background default stay as they are.");
                await BackgroundDefaults.ApplyAsync(c.Db, c.Services.GetRequiredService<ConnectorRegistry>(), secrets, c.CancellationToken);
            }),
    ];
}
