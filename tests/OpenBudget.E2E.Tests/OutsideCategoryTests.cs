using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A booking on one of the project's accounts that carries another project's category: the list marks its pill as a
/// category outside the project (full path, the other project's name), and the filter "Category outside this project"
/// finds exactly those bookings.
/// </summary>
[TestFixture]
public sealed class OutsideCategoryTests : EndToEndTestBase
{
    private const int OtherProjectId = 61;
    private const string OtherProjectName = "Personal Example";

    [SetUp]
    public async Task SeedOtherProject()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        db.CategoryGroups.Add(new CategoryGroup { Id = 400, OwnerUserId = E2ESeed.OwnerUserId, Name = "Gadgets" });
        db.Categories.Add(new Category { Id = 410, OwnerUserId = E2ESeed.OwnerUserId, Name = "Smart Home", GroupId = 400, Type = 1 });
        db.Projects.Add(new Project { Id = OtherProjectId, Name = OtherProjectName, OwnerUserId = E2ESeed.OwnerUserId, SortOrder = 5, CreatedAt = DateTime.UtcNow });
        EditingSeed.Add(db, 130, EditingSeed.BankAccountId, -49_90, "Example Electronics", new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc), category: 410);
        await db.SaveChangesAsync();
        db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = OtherProjectId, CategoryGroupId = 400 });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Another_projects_category_is_marked_and_found_by_the_filter()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        var rows = Page.GetByTestId("tx-row");
        var outsidePill = rows.Filter(new() { HasText = "Example Electronics" }).GetByTestId("tx-category");
        await Expect(outsidePill).ToHaveAttributeAsync("data-outside", "true");
        await Expect(outsidePill).ToContainTextAsync("Gadgets › Smart Home");
        await Expect(outsidePill).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex($"Category of project {OtherProjectName}"));
        var ownPill = rows.Filter(new() { HasText = "Example Employer" }).GetByTestId("tx-category");
        await Expect(ownPill).ToBeVisibleAsync();
        await Expect(ownPill).Not.ToHaveAttributeAsync("data-outside", "true");

        // The whole project's list leaves such bookings out; the filter finds them on the project's accounts.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        await Expect(rows.Filter(new() { HasText = "Example Employer" })).ToBeVisibleAsync();
        await Page.GetByTestId("filter-toggle").ClickAsync();
        await Page.GetByTestId("filter-outside").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("[?&]out=1"));
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.First).ToContainTextAsync("Example Electronics");
        await Expect(Page.GetByTestId("filter-count")).ToHaveTextAsync("1");

        await Page.GetByTestId("filter-outside").ClickAsync();
        await Expect(rows.Filter(new() { HasText = "Example Employer" })).ToBeVisibleAsync();
    }
}
