using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>One PortfolioPerformance portfolio of the user on the settings page.</summary>
public sealed record PortfolioAccountRow(
    string Uuid,
    string Name,
    bool Retired,
    string? LinkedBankDepot,
    bool Show,
    bool ShowIsDefault,
    int? ProjectId,
    string? DepotAccountName,
    string? LedgerAccountName,
    bool TakenOver,
    PortfolioTakeoverCandidate? PendingTakeover);

public sealed record PortfolioProjectOption(int Id, string Name);

public sealed record PortfolioAccountSettings(
    bool HasFile, IReadOnlyList<PortfolioAccountRow> Portfolios, IReadOnlyList<PortfolioProjectOption> Projects);

/// <summary>
/// The owner's settings for showing their PortfolioPerformance portfolios as accounts
/// (<see cref="PpPortfolioSetting"/>): the switch, the target project and taking over accounts that already exist.
/// Owner-only; every change that affects the accounts recomputes them (<see cref="PortfolioAccountSync"/>).
/// </summary>
public sealed class PortfolioAccountSettingsService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly PortfolioAccountSync _sync;
    private readonly IPortfolioFileSource _files;

    public PortfolioAccountSettingsService(IDbContextFactory<BudgetDbContext> dbFactory, PortfolioAccountSync sync, IPortfolioFileSource files)
    {
        _dbFactory = dbFactory;
        _sync = sync;
        _files = files;
    }

    public async Task<PortfolioAccountSettings?> GetAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireOwner(ownerId);

        var portfolios = await db.PpPortfolios.AsNoTracking().Where(p => p.OwnerUserId == ownerId).OrderBy(p => p.Name).ToListAsync(ct);
        var settings = await db.PpPortfolioSettings.AsNoTracking().Where(s => s.OwnerUserId == ownerId).ToListAsync(ct);
        var links = await db.PpPortfolioLinks.AsNoTracking().Where(l => l.OwnerUserId == ownerId).ToListAsync(ct);
        var accountIds = settings.SelectMany(s => new[] { s.DepotAccountId, s.LedgerAccountId }).OfType<long>()
            .Concat(links.Select(l => l.AccountId)).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "?", ct);

        var rows = new List<PortfolioAccountRow>();
        foreach (var p in portfolios)
        {
            var s = settings.FirstOrDefault(x => x.PpPortfolioUuid == p.Uuid);
            var link = links.FirstOrDefault(l => l.PpPortfolioUuid == p.Uuid);
            var show = s?.ShowAsAccount ?? PortfolioAccountSync.DefaultShow(link != null, p.IsRetired);
            PortfolioTakeoverCandidate? pending = null;
            if (s?.DepotAccountId == null && s?.LedgerAccountId == null && s?.TakeoverDeclined != true)
            {
                var candidate = await PortfolioAccountSync.FindTakeoverCandidateAsync(db, ownerId, p.Name, ct);
                if (candidate.Any) pending = candidate;
            }
            rows.Add(new PortfolioAccountRow(
                p.Uuid, p.Name, p.IsRetired,
                link != null ? names.GetValueOrDefault(link.AccountId) : null,
                show, s?.ShowAsAccount == null, s?.ProjectId,
                s?.DepotAccountId is { } d ? names.GetValueOrDefault(d) : null,
                s?.LedgerAccountId is { } l ? names.GetValueOrDefault(l) : null,
                s?.TakenOver == true, pending));
        }

        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PortfolioProjectOption(p.Id, p.Name))
            .ToListAsync(ct);

        bool hasFile;
        await using (var file = await _files.OpenLatestAsync(ownerId, ct)) hasFile = file != null;
        return new PortfolioAccountSettings(hasFile, rows, projects);
    }

    /// <summary>Switches showing the portfolio as accounts on or off and recomputes.</summary>
    public async Task SetShowAsync(string ownerId, string uuid, bool show, CancellationToken ct = default)
    {
        await UpdateAsync(ownerId, uuid, s => s.ShowAsAccount = show, ct);
        await _sync.SyncAsync(ownerId, ct);
    }

    /// <summary>Sets the project the portfolio's accounts belong to; existing accounts move there from the previous one.</summary>
    public async Task SetProjectAsync(string ownerId, string uuid, int? projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireOwner(ownerId);
        var setting = await GetOrAddAsync(db, ownerId, uuid, ct);
        var previous = setting.ProjectId;
        setting.ProjectId = projectId;
        var accounts = new[] { setting.DepotAccountId, setting.LedgerAccountId }.OfType<long>().ToList();
        if (accounts.Count > 0 && previous != projectId)
        {
            if (previous is { } old)
                db.ProjectAccountAssignments.RemoveRange(await db.ProjectAccountAssignments
                    .Where(a => a.ProjectId == old && accounts.Contains(a.AccountId)).ToListAsync(ct));
            if (projectId is { } pid)
            {
                var already = await db.ProjectAccountAssignments.Where(a => a.ProjectId == pid && accounts.Contains(a.AccountId))
                    .Select(a => a.AccountId).ToListAsync(ct);
                foreach (var id in accounts.Except(already))
                    db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = pid, AccountId = id, Scope = AccountScope.Exclusive, InCashflow = false });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Makes the existing accounts named like the portfolio its accounts (keeping their history) and recomputes.</summary>
    public async Task TakeOverAsync(string ownerId, string uuid, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.Scope?.RequireOwner(ownerId);
            var portfolio = await db.PpPortfolios.AsNoTracking().FirstOrDefaultAsync(p => p.OwnerUserId == ownerId && p.Uuid == uuid, ct)
                ?? throw new InvalidOperationException("Unknown portfolio.");
            var candidate = await PortfolioAccountSync.FindTakeoverCandidateAsync(db, ownerId, portfolio.Name, ct);
            if (!candidate.Any) throw new InvalidOperationException("There is no account to take over.");
            var setting = await GetOrAddAsync(db, ownerId, uuid, ct);
            if (setting.DepotAccountId != null || setting.LedgerAccountId != null)
                throw new InvalidOperationException("The portfolio already has accounts.");
            setting.DepotAccountId = candidate.DepotAccountId;
            setting.LedgerAccountId = candidate.LedgerAccountId;
            setting.TakenOver = true;
            setting.ShowAsAccount = true;
            var ids = new[] { candidate.DepotAccountId, candidate.LedgerAccountId }.OfType<long>().ToList();
            foreach (var account in await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
                account.Provider = AccountProviders.PortfolioPerformance;
            await db.SaveChangesAsync(ct);
        }
        await _sync.SyncAsync(ownerId, ct);
    }

    /// <summary>Creates new accounts for the portfolio instead of taking the existing ones over.</summary>
    public async Task DeclineTakeoverAsync(string ownerId, string uuid, CancellationToken ct = default)
    {
        await UpdateAsync(ownerId, uuid, s =>
        {
            s.TakeoverDeclined = true;
            s.ShowAsAccount = true;
        }, ct);
        await _sync.SyncAsync(ownerId, ct);
    }

    private async Task UpdateAsync(string ownerId, string uuid, Action<PpPortfolioSetting> change, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireOwner(ownerId);
        change(await GetOrAddAsync(db, ownerId, uuid, ct));
        await db.SaveChangesAsync(ct);
    }

    private static async Task<PpPortfolioSetting> GetOrAddAsync(BudgetDbContext db, string ownerId, string uuid, CancellationToken ct)
    {
        var setting = await db.PpPortfolioSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerId && s.PpPortfolioUuid == uuid, ct);
        if (setting != null) return setting;
        setting = new PpPortfolioSetting { OwnerUserId = ownerId, PpPortfolioUuid = uuid };
        db.PpPortfolioSettings.Add(setting);
        return setting;
    }
}
