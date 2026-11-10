using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Gear › Rules: reaching the page from the gear menu, creating a rule from a transaction ("Create rule from this…")
/// and applying it to the existing uncategorized rows after the preview. Uses the editing world (<see cref="EditingSeed"/>):
/// three uncategorized "Example Grocer" rows on a connector account.
/// </summary>
[TestFixture]
public sealed class RulesTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>());

    [Test]
    public async Task The_gear_menu_leads_to_the_rules_page()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        var gear = Page.GetByTestId("gear-menu");
        await gear.Locator("summary").ClickAsync();
        await gear.GetByTestId("gear-rules").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/rules$"));
        await Expect(Page.GetByTestId("rules-empty")).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_rule_created_from_a_transaction_categorizes_the_existing_rows()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized");
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "−25,00" }).ClickAsync();
        await Page.GetByTestId("tx-details-create-rule").ClickAsync();

        // The editor opens prefilled with the payee.
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/rules"));
        await Expect(Page.GetByTestId("rule-field-payee")).ToHaveValueAsync("Example Grocer");
        await Page.GetByTestId("rule-field-category").SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = "Groceries" });
        await Page.GetByTestId("rule-save").ClickAsync();

        var row = Page.GetByTestId("rule-row");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row.GetByTestId("rule-category")).ToHaveTextAsync("Groceries");
        await Expect(row.GetByTestId("rule-matches")).ToHaveTextAsync("3");

        await Page.GetByTestId("rules-apply-existing").ClickAsync();
        await Expect(Page.GetByTestId("rules-apply-preview")).ToContainTextAsync("3 uncategorized");
        await Page.GetByTestId("rules-apply-confirm").ClickAsync();
        await Expect(Page.GetByTestId("rules-status")).ToContainTextAsync("3 transaction(s) categorized");

        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.Name == "Example Grocer").ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows.All(t => t.CategoryId == 200 && t.CategorySetBy == TransactionCategorySource.Rule), Is.True);

        // They left the Uncategorized list.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized");
        await Expect(Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Grocer" })).ToHaveCountAsync(0);
    }
}
