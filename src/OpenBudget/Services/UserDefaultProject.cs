using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Gives every user a project of their own: a user who owns no project gets one named after them (display name, else
/// username), on the source holding their MoneyMoney data, else their oldest source, else a new source. It is their
/// default project for new accounts, so the MoneyMoney import and bank-login accounts land in it; accounts of their
/// sources that no project shows yet are assigned to it right away. Idempotent: users who own a project are skipped.
/// </summary>
public static class UserDefaultProject
{
    /// <summary>Creates the project for <paramref name="userId"/> when they own none. Returns the new project's id, else null.</summary>
    public static async Task<int?> EnsureAsync(IDbContextFactory<BudgetDbContext> system, string userId, CancellationToken ct = default)
    {
        await using var db = await system.CreateDbContextAsync(ct);
        if (await db.Projects.AnyAsync(p => p.OwnerUserId == userId, ct)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null) return null;

        var source = await db.ImportSources.Where(s => s.OwnerUserId == userId)
            .OrderBy(s => s.LastUploadAt == null).ThenBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (source == null)
        {
            source = new ImportSource { Name = await FreeSourceNameAsync(db, ct), OwnerUserId = userId };
            db.ImportSources.Add(source);
            await db.SaveChangesAsync(ct);
        }

        var maxSort = await db.Projects.MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        var project = new Project
        {
            Name = AppUser.ShownName(user.DisplayName, user.UserName),
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSort + 1,
            SourceId = source.Id,
            OwnerUserId = userId,
            IsDefaultForNewAccounts = true,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        var ownedSources = await db.ImportSources.Where(s => s.OwnerUserId == userId).Select(s => s.Id).ToListAsync(ct);
        var accounts = await db.Accounts.Where(a => ownedSources.Contains(a.SourceId))
            .Select(a => a.Id).ToListAsync(ct);
        await DefaultProjectAssigner.AssignNewAccountsAsync(db, userId, accounts, ct);
        return project.Id;
    }

    /// <summary>Runs <see cref="EnsureAsync"/> for every user; returns how many projects were created.</summary>
    public static async Task<int> EnsureAllAsync(IDbContextFactory<BudgetDbContext> system, CancellationToken ct = default)
    {
        List<string> userIds;
        await using (var db = await system.CreateDbContextAsync(ct))
            userIds = await db.Users.Where(u => !db.Projects.Any(p => p.OwnerUserId == u.Id)).Select(u => u.Id).ToListAsync(ct);
        var created = 0;
        foreach (var id in userIds)
            if (await EnsureAsync(system, id, ct) != null) created++;
        return created;
    }

    private static async Task<string> FreeSourceNameAsync(BudgetDbContext db, CancellationToken ct)
    {
        while (true)
        {
            var name = "data-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            if (!await db.ImportSources.AnyAsync(s => s.Name == name, ct)) return name;
        }
    }
}
