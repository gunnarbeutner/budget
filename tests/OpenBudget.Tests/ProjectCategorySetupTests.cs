using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class ProjectCategorySetupTests
{
    [Fact]
    public async Task A_group_and_a_category_sharing_an_id_get_one_income_assignment()
    {
        await using var t = new TestDb();
        // Imported groups and categories are numbered separately, so a group and a category can carry the same id.
        t.AddGroup(30, name: "Income");
        t.AddLeaf(30, parent: 30, name: "Salary");
        t.AddProject(1);
        t.AddTransaction(1, 30, 2500_00, new DateTime(2026, 3, 1));
        await t.Db.SaveChangesAsync();

        await ProjectCategorySetup.ApplySuggestionsAsync(t.Db, TestDb.DefaultOwner, 1, new DateTime(2026, 4, 1));

        var assignment = Assert.Single(await t.Db.CategoryTypeAssignments.Where(a => a.ProjectId == 1 && a.CategoryId == 30).ToListAsync());
        Assert.True(assignment.IsIncome);
    }
}
