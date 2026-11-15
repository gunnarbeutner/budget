using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services.DataFixes;

/// <summary>What a data fix gets to work with: an unscoped context (inside the fix's transaction when it has one).</summary>
public sealed record DataFixContext(BudgetDbContext Db, IServiceProvider Services, DateTime NowUtc, ILogger Logger, CancellationToken CancellationToken);

/// <summary>
/// A repair of existing data that runs once per database. <see cref="Name"/> is stable: it is what
/// <c>data_fixes</c> records. A transactional fix works only through <see cref="DataFixContext.Db"/>, so its changes
/// and its record commit together; a non-transactional one (it opens its own contexts or transaction) is recorded
/// after it returns, so it must be safe to run again after a crash in between.
/// </summary>
public sealed record DataFix(string Name, string Description, Func<DataFixContext, Task> RunAsync, bool Transactional = true);

/// <summary>
/// Runs, at startup, each registered data fix that this database has not recorded yet, in list order, and records it.
/// A fix that throws is logged and not recorded, and the fixes after it wait too (a later fix may build on an earlier
/// one); all of them are tried again at the next start. Startup goes on either way.
/// </summary>
public sealed class DataFixRunner(SystemDbContextFactory system, IServiceProvider services, ILogger<DataFixRunner> logger, TimeProvider time)
{
    // Several app instances on one database (tests) may start together.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Runs the pending fixes of <paramref name="fixes"/>; returns the names of those that ran and were recorded.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<DataFix> fixes, CancellationToken ct = default)
    {
        var ran = new List<string>();
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var fix in fixes)
            {
                try
                {
                    if (await RunOneAsync(fix, ct)) ran.Add(fix.Name);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Data fix {Name} failed; it and the fixes after it run again at the next start", fix.Name);
                    break;
                }
            }
        }
        finally
        {
            Gate.Release();
        }
        return ran;
    }

    private async Task<bool> RunOneAsync(DataFix fix, CancellationToken ct)
    {
        await using var db = await system.CreateDbContextAsync(ct);
        if (await db.AppliedDataFixes.AnyAsync(f => f.Name == fix.Name, ct)) return false;

        var context = new DataFixContext(db, services, time.GetUtcNow().UtcDateTime, logger, ct);
        if (fix.Transactional)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await fix.RunAsync(context);
            db.AppliedDataFixes.Add(new AppliedDataFix { Name = fix.Name, AppliedAt = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await fix.RunAsync(context);
            db.ChangeTracker.Clear();
            db.AppliedDataFixes.Add(new AppliedDataFix { Name = fix.Name, AppliedAt = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Data fix {Name} applied: {Description}", fix.Name, fix.Description);
        return true;
    }
}
