using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// UpdateBudgetAsync saves a budget's editable metadata (name/year/notes/active). It must NOT
/// rewrite the monthly entries on the detached graph the editor hands it — those go through
/// SaveBudgetEntriesAsync — and it must keep the one-active-per-(project,year) invariant.
/// </summary>
public class UpdateBudgetAsyncTests
{
    [Fact]
    public async Task Persists_scalar_edits_without_rewriting_monthly_entries()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        var created = t.AddBudget(1, 2026, (101, CategoryItemType.Category, 50m));
        await t.Db.SaveChangesAsync();

        // Load a detached graph the way the editor does, edit the label, and (the trap) mutate a
        // monthly entry in memory. A blanket .Update(budget) would persist the 999; we must not.
        var loaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.NotNull(loaded);
        loaded!.Name = "Revised";
        loaded.Notes = "note";
        loaded.MonthlyEntries.First().SetMonthAmount(1, 999m);

        await t.Entries.UpdateBudgetAsync(loaded);

        var amounts = await t.Entries.GetMonthlyBudgetAmountsAsync(created.Id);
        Assert.Equal(50m, amounts[(101L, CategoryItemType.Category)][0]); // unchanged, not 999

        var reloaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal("Revised", reloaded!.Name);
        Assert.Equal("note", reloaded.Notes);
        Assert.NotNull(reloaded.LastModifiedAt);
    }

    [Fact]
    public async Task Activating_a_budget_deactivates_its_year_siblings()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var a = t.AddBudget(1, 2026);
        await t.Db.SaveChangesAsync();
        var b = t.AddBudget(1, 2026); // AddBudget marks every budget active, so both start active
        await t.Db.SaveChangesAsync();

        var loadedA = await t.Entries.GetBudgetAsync(a.Id);
        loadedA!.IsActive = true;
        await t.Entries.UpdateBudgetAsync(loadedA);

        Assert.True((await t.Entries.GetBudgetAsync(a.Id))!.IsActive);
        Assert.False((await t.Entries.GetBudgetAsync(b.Id))!.IsActive);
    }
}
