using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Auth;

/// <summary>
/// Name-only view of the user table, for the sharing UI (pick a user, show who owns/shares a
/// project). Exposes nothing but ids, user names and display names.
/// </summary>
public sealed class UserDirectory
{
    private readonly SystemDbContextFactory _system;

    public UserDirectory(SystemDbContextFactory system) => _system = system;

    public async Task<List<(string Id, string Name)>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var rows = await db.Users.AsNoTracking().OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.UserName ?? "")).ToList();
    }

    /// <summary>Display names (falling back to the user name) by user id.</summary>
    public async Task<Dictionary<string, string>> GetNamesAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct(StringComparer.Ordinal).ToList();
        if (idList.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        await using var db = await _system.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => idList.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => AppUser.ShownName(u.DisplayName, u.UserName), StringComparer.Ordinal, ct);
    }
}
