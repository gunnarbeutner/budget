using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>How often a MoneyMoney category budget applies (<c>budgetInterval</c>).</summary>
public enum MoneyMoneyBudgetInterval
{
    Monthly = 0,
    Quarterly = 1,
    Yearly = 2,
    Total = 3,
}

/// <summary>A category budget as MoneyMoney stores it in <c>categories.userdata</c>.</summary>
public sealed record MoneyMoneyCategoryBudget(decimal Amount, MoneyMoneyBudgetInterval Interval);

/// <summary>What the budget import did: entries written and "in total" budgets that have no monthly equivalent.</summary>
public sealed record MoneyMoneyBudgetSummary(int Imported, int SkippedTotal);

/// <summary>
/// Turns MoneyMoney's per-category budgets into one OpenBudget budget named "MoneyMoney" for the current year
/// in the owner's default project, active unless the project has an active budget for that year. Monthly budgets apply to every month; quarterly and yearly ones are spread evenly
/// (rounded to cents, the remainder in December so the year adds up). "In total" budgets are skipped and counted.
/// A year that already has a "MoneyMoney" budget in that project is left alone.
/// </summary>
public static class MoneyMoneyBudgetImport
{
    public const string BudgetName = "MoneyMoney";

    /// <summary>
    /// Reads a <c>userdata</c> blob: a list of <c>&lt;length&gt;:&lt;text&gt;,</c> items forming key/value pairs.
    /// Returns null without a non-zero <c>budget</c> or with an unknown interval; malformed input ends parsing quietly.
    /// </summary>
    public static MoneyMoneyCategoryBudget? Parse(byte[]? userdata)
    {
        if (userdata == null || userdata.Length == 0) return null;
        var items = new List<string>();
        var pos = 0;
        while (pos < userdata.Length)
        {
            var colon = Array.IndexOf(userdata, (byte)':', pos);
            if (colon < 0) break;
            if (!int.TryParse(Encoding.ASCII.GetString(userdata, pos, colon - pos), NumberStyles.None, CultureInfo.InvariantCulture, out var len)) break;
            var start = colon + 1;
            if (len < 0 || start + len > userdata.Length) break;
            items.Add(Encoding.UTF8.GetString(userdata, start, len));
            pos = start + len;
            if (pos < userdata.Length && userdata[pos] == (byte)',') pos++;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < items.Count; i += 2) values[items[i]] = items[i + 1];

        if (!values.TryGetValue("budget", out var text)
            || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || amount == 0) return null;
        var interval = values.TryGetValue("budgetInterval", out var iv) && int.TryParse(iv, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (interval is < 0 or > 3) return null;
        return new MoneyMoneyCategoryBudget(Math.Abs(amount), (MoneyMoneyBudgetInterval)interval);
    }

    /// <summary>The twelve monthly amounts for a budget (null for "in total").</summary>
    public static decimal[]? Months(MoneyMoneyCategoryBudget budget)
    {
        var yearly = budget.Interval switch
        {
            MoneyMoneyBudgetInterval.Monthly => budget.Amount * 12,
            MoneyMoneyBudgetInterval.Quarterly => budget.Amount * 4,
            MoneyMoneyBudgetInterval.Yearly => budget.Amount,
            MoneyMoneyBudgetInterval.Total => null,
            _ => (decimal?)null,
        };
        if (yearly is not { } total) return null;
        var monthly = budget.Interval == MoneyMoneyBudgetInterval.Monthly ? budget.Amount : Math.Round(total / 12, 2, MidpointRounding.AwayFromZero);
        var months = Enumerable.Repeat(monthly, 12).ToArray();
        months[11] = total - (monthly * 11);
        return months;
    }

    /// <summary>Imports the budgets of the MoneyMoney database at <paramref name="sqlitePath"/> into the default project of <paramref name="ownerId"/>.</summary>
    public static async Task<MoneyMoneyBudgetSummary> ImportAsync(BudgetDbContext db, string ownerId, string sqlitePath, int year, CancellationToken ct)
    {
        var idSpace = await RowIds.IdSpaceAsync(db, ownerId, ct);
        var budgets = new List<(long CategoryId, MoneyMoneyCategoryBudget Budget)>();
        await using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await src.OpenAsync(ct);
            await using var probe = src.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('categories') WHERE name = 'userdata'";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) return new MoneyMoneyBudgetSummary(0, 0);
            await using var cmd = src.CreateCommand();
            cmd.CommandText = "SELECT rowid, userdata FROM categories WHERE userdata IS NOT NULL";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var blob = r.GetValue(1) switch { byte[] b => b, string s => Encoding.UTF8.GetBytes(s), _ => null };
                if (Parse(blob) is { } budget) budgets.Add((RowIds.Namespace(idSpace, r.GetInt64(0)), budget));
            }
        }
        var skipped = budgets.Count(b => b.Budget.Interval == MoneyMoneyBudgetInterval.Total);
        var usable = budgets.Where(b => b.Budget.Interval != MoneyMoneyBudgetInterval.Total).ToList();
        if (usable.Count == 0) return new MoneyMoneyBudgetSummary(0, skipped);

        var projectId = await db.Projects.Where(p => p.OwnerUserId == ownerId && p.IsDefaultForNewAccounts)
            .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        if (projectId is not { } pid) return new MoneyMoneyBudgetSummary(0, skipped);
        if (await db.Budgets.AnyAsync(b => b.ProjectId == pid && b.Year == year && b.Name == BudgetName, ct)) return new MoneyMoneyBudgetSummary(0, skipped);

        var known = (await db.Categories.Where(c => c.OwnerUserId == ownerId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        // Active unless the project already has an active budget for the year.
        var active = !await db.Budgets.AnyAsync(b => b.ProjectId == pid && b.Year == year && b.IsActive, ct);
        var budgetRow = new Budget { ProjectId = pid, Year = year, Name = BudgetName, IsActive = active, CreatedAt = DateTime.UtcNow };
        db.Budgets.Add(budgetRow);
        await db.SaveChangesAsync(ct);
        var imported = 0;
        foreach (var (categoryId, budget) in usable.Where(b => known.Contains(b.CategoryId)))
        {
            var entry = new MonthlyBudgetEntry { BudgetId = budgetRow.Id, CategoryId = categoryId, ItemType = CategoryItemType.Category };
            var months = Months(budget)!;
            for (var m = 1; m <= 12; m++) entry.SetMonthAmount(m, months[m - 1]);
            db.MonthlyBudgetEntries.Add(entry);
            imported++;
        }
        await db.SaveChangesAsync(ct);
        return new MoneyMoneyBudgetSummary(imported, skipped);
    }
}
