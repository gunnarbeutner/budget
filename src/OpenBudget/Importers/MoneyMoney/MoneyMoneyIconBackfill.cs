using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>What <see cref="MoneyMoneyIconBackfill.RunAsync"/> found and (with apply) set.</summary>
public sealed record MoneyMoneyIconBackfillResult(int AccountsSet, int GroupsSet, int KeptOwnChoice, int Mismatches, int Unmatched);

/// <summary>
/// Gives accounts and account groups imported from MoneyMoney the icon and colour they have there, for data imported
/// before the import took icons along. Rows are matched by the import's id (<see cref="RowIds.Namespace"/> of the
/// MoneyMoney rowid) and must still carry the MoneyMoney name; only rows whose icon and colour are both unset change.
/// </summary>
public static class MoneyMoneyIconBackfill
{
    private sealed record Source(long RowId, string? Name, MoneyMoneyIconLook Look);

    public static async Task<MoneyMoneyIconBackfillResult> RunAsync(
        BudgetDbContext db, string ownerId, string plainDatabasePath, bool apply, List<string> report, CancellationToken ct = default)
    {
        var state = await RowIds.PeekStateAsync(db, ownerId, ct);
        if (state.IdSpace <= 0) throw new InvalidOperationException("The user has no imported MoneyMoney data.");
        int idSpace = state.IdSpace;

        var accounts = Read(plainDatabasePath, "accounts");
        var groups = Read(plainDatabasePath, "account_groups");
        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int accountsSet = 0, groupsSet = 0, kept = 0, mismatches = 0, unmatched = 0;

        var accountIds = accounts.Select(s => RowIds.Namespace(idSpace, s.RowId)).ToList();
        var ownAccounts = await db.Accounts.IgnoreQueryFilters().Where(a => a.OwnerUserId == ownerId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        foreach (var s in accounts)
        {
            if (!ownAccounts.TryGetValue(RowIds.Namespace(idSpace, s.RowId), out var a)) { unmatched++; continue; }
            if (!string.Equals(a.Name, s.Name, StringComparison.Ordinal))
            {
                mismatches++;
                report.Add($"Skipped account {s.RowId}: '{s.Name}' in MoneyMoney, '{a.Name}' in OpenBudget.");
                continue;
            }
            if (a.Icon != null || a.Color != null) { kept++; continue; }
            a.Icon = s.Look.Icon;
            a.Color = s.Look.Color;
            accountsSet++;
            Count(shapes, "account", s.Look);
        }

        var groupIds = groups.Select(s => RowIds.Namespace(idSpace, s.RowId)).ToList();
        var ownGroups = await db.AccountGroups.IgnoreQueryFilters().Where(g => g.OwnerUserId == ownerId && groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        foreach (var s in groups)
        {
            if (!ownGroups.TryGetValue(RowIds.Namespace(idSpace, s.RowId), out var g)) { unmatched++; continue; }
            if (!string.Equals(g.Name, s.Name, StringComparison.Ordinal))
            {
                mismatches++;
                report.Add($"Skipped group {s.RowId}: '{s.Name}' in MoneyMoney, '{g.Name}' in OpenBudget.");
                continue;
            }
            if (g.Icon != null || g.Color != null) { kept++; continue; }
            g.Icon = s.Look.Icon;
            g.Color = s.Look.Color;
            groupsSet++;
            Count(shapes, "group", s.Look);
        }

        foreach (var (key, n) in shapes) report.Add(string.Create(CultureInfo.InvariantCulture, $"{key}: {n}"));
        report.Add(string.Create(CultureInfo.InvariantCulture,
            $"{(apply ? "Set" : "Would set")} {accountsSet} account(s) and {groupsSet} group(s); {kept} kept their own icon, {mismatches} name mismatch(es), {unmatched} not found."));
        if (apply) await db.SaveChangesAsync(ct);
        return new MoneyMoneyIconBackfillResult(accountsSet, groupsSet, kept, mismatches, unmatched);
    }

    private static void Count(SortedDictionary<string, int> counts, string kind, MoneyMoneyIconLook look)
    {
        var key = $"{kind} {look.Icon ?? "(type default)"} {look.Color}";
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    /// <summary>The rows of <paramref name="table"/> carrying an icon this mapping understands.</summary>
    private static List<Source> Read(string path, string table)
    {
        var rows = new List<Source>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (table == "accounts") cmd.CommandText = "SELECT rowid, name, icon FROM accounts";
        else cmd.CommandText = "SELECT rowid, name, icon FROM account_groups";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (MoneyMoneyIcons.Parse(r.IsDBNull(2) ? null : r.GetString(2)) is { } look)
                rows.Add(new Source(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), look));
        }
        return rows;
    }
}
