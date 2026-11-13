using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A guest with a project of their own on their own source, editing a project shared by the owner: the category
/// picker of the shared project lists that project's source's categories, also after switching over from the guest's
/// own project in the header.
/// </summary>
[TestFixture]
public sealed class SharedProjectCategorizingTests : EndToEndTestBase
{
    private const int GuestSourceId = E2ESeed.SecondIdSpace;
    private const int GuestProjectId = 60;
    private const string GuestProjectName = "Guest Household";
    private const string GuestCategory = "Guest Hobbies";

    [SetUp]
    public async Task SeedGuestWorld()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        db.CategoryGroups.Add(new CategoryGroup { Id = 300, OwnerUserId = guest.Id, Name = "Guest Things" });
        db.Categories.Add(new Category { Id = 310, OwnerUserId = guest.Id, Name = GuestCategory, GroupId = 300, Type = 1 });
        db.Projects.Add(new Project { Id = GuestProjectId, Name = GuestProjectName, OwnerUserId = guest.Id, SortOrder = 0, CreatedAt = DateTime.UtcNow });
        var account = RowIds.Namespace(GuestSourceId, 1);
        db.Accounts.Add(new Account { Id = account, OwnerUserId = guest.Id, Name = "Guest Checking", Provider = "fake", Type = 1, Currency = "EUR", Balances = "11:100.000000,3:EUR," });
        db.Transactions.Add(new Transaction
        {
            Id = RowIds.Namespace(GuestSourceId, 500),
            OwnerUserId = guest.Id,
            AccountId = account,
            Amount = -7_00,
            Currency = "EUR",
            Name = "Example Florist",
            Timestamp = ((DateTimeOffset)new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = GuestProjectId, AccountId = account });
        db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = GuestProjectId, CategoryGroupId = 300 });
        db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Editor });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_shared_projects_picker_lists_its_own_sources_categories_after_a_switch()
    {
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        var picker = Page.GetByTestId("category-picker");
        await GoToAsync($"/projects/{GuestProjectId}/browse?year=2026&month=3&sel=uncategorized");
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Florist" }).ClickAsync();
        await Page.Keyboard.PressAsync("c");
        await Expect(picker.GetByTestId("category-picker-option").Filter(new() { HasText = GuestCategory })).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");

        // A client-side navigation reuses the browse page, as picking the project in the header's switcher does.
        await Page.EvaluateAsync($"Blazor.navigateTo('/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized')");
        await Expect(Page.GetByTestId("project-selector")).ToContainTextAsync(E2ESeed.ProjectName);
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "−25,00" }).ClickAsync();
        await Page.Keyboard.PressAsync("c");
        await Expect(picker).ToBeVisibleAsync();
        await Expect(picker.GetByTestId("category-picker-option").Filter(new() { HasText = "Groceries" })).ToBeVisibleAsync();
        await Expect(picker.GetByTestId("category-picker-option").Filter(new() { HasText = GuestCategory })).ToHaveCountAsync(0);

        await picker.GetByTestId("category-picker-option").Filter(new() { HasText = "Groceries" }).ClickAsync();
        await Expect(picker).ToHaveCountAsync(0);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 101)).CategoryId, Is.EqualTo(200));
    }
}
