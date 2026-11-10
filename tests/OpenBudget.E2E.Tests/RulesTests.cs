using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Categorisation rules live in a category's rules bar on Transactions (right-click a category › Rules…): inline rows,
/// a live preview of the bookings a draft matches, "Create rule from this…" on a booking, and applying the rules to
/// the uncategorized rows. Uses the editing world (<see cref="EditingSeed"/>): three uncategorized "Example Grocer"
/// rows on a connector account.
/// </summary>
[TestFixture]
public sealed class RulesTests : EndToEndTestBase
{
    private static string UncategorizedUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized";

    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>());

    private ILocator Category(string name) =>
        Page.GetByTestId("project-sidebar").GetByTestId("tree-category").Filter(new() { HasText = name }).First;

    [Test]
    public async Task Right_clicking_a_category_opens_its_rules_bar()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        await Category("Groceries").ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-rules").ClickAsync();

        var bar = Page.GetByTestId("rules-bar");
        await Expect(bar).ToBeVisibleAsync();
        await Expect(bar).ToContainTextAsync("Groceries");
        await Expect(bar.GetByTestId("rules-empty")).ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("sel=cat-200.*rules=1|rules=1.*sel=cat-200"));

        await bar.GetByTestId("rules-close").ClickAsync();
        await Expect(Page.GetByTestId("rules-bar")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_add_rule_button_takes_clicks_across_its_whole_area()
    {
        await Page.SetViewportSizeAsync(2000, 900);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=cat-200&rules=1");
        var add = Page.GetByTestId("rules-add");
        await Expect(add).ToBeVisibleAsync();
        // Every point inside the button's box must hit the button itself (or its icon/text), not something on top of it.
        var misses = await add.EvaluateAsync<string[]>(@"b => {
            const r = b.getBoundingClientRect(), out = [];
            for (let fx = 0.05; fx < 1; fx += 0.15) for (let fy = 0.1; fy < 1; fy += 0.2) {
                const x = r.left + r.width * fx, y = r.top + r.height * fy, hit = document.elementFromPoint(x, y);
                if (!hit || !b.contains(hit)) out.push(Math.round(fx * 100) + '%,' + Math.round(fy * 100) + '% -> ' + (hit ? hit.tagName + '.' + hit.className : 'none'));
            }
            return out;
        }");
        Assert.That(misses, Is.Empty);
    }

    [Test]
    public async Task The_keyboard_opens_the_category_menu_and_the_gear_has_no_rules_entry()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        await Category("Groceries").FocusAsync();
        await Page.Keyboard.PressAsync("Shift+F10");
        await Expect(Page.GetByTestId("ctx-rules")).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");

        var gear = Page.GetByTestId("gear-menu");
        await gear.Locator("summary").ClickAsync();
        await Expect(gear.GetByTestId("gear-project-settings")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("gear-rules")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_old_rules_page_url_lands_on_transactions()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/rules");
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/browse"));
    }

    [Test]
    public async Task A_draft_previews_its_matches_and_escape_discards_it()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=cat-200&rules=1");
        var bar = Page.GetByTestId("rules-bar");
        await bar.GetByTestId("rules-add").ClickAsync();
        var payee = bar.GetByTestId("rule-payee").Last;
        await payee.FillAsync("example grocer");

        await Expect(bar.GetByTestId("rules-preview-count")).ToHaveTextAsync("3 bookings match");
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Grocer" })).ToHaveCountAsync(3);

        await payee.PressAsync("Escape");
        await Expect(bar.GetByTestId("rule-row")).ToHaveCountAsync(0);
        await Expect(bar.GetByTestId("rules-preview-count")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Grocer" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_rule_created_from_a_transaction_categorizes_the_existing_rows()
    {
        await GoToAsync(UncategorizedUrl);
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "−25,00" }).ClickAsync();
        await Page.GetByTestId("tx-details-create-rule").ClickAsync();

        // No category yet: the picker asks which one the rule should set.
        var search = Page.GetByTestId("category-picker-search");
        await Expect(search).ToBeFocusedAsync();
        await search.FillAsync("Groceries");
        await Expect(Page.GetByTestId("category-picker-option").First).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Enter");

        // The category's rules bar with a prefilled draft row and its live preview.
        var bar = Page.GetByTestId("rules-bar");
        await Expect(bar).ToContainTextAsync("Groceries");
        var payee = bar.GetByTestId("rule-payee");
        await Expect(payee).ToHaveValueAsync("Example Grocer");
        await Expect(bar.GetByTestId("rule-expression")).ToHaveValueAsync("name:\"Example Grocer\"");
        await Expect(bar.GetByTestId("rules-preview-count")).ToHaveTextAsync("3 bookings match");

        await payee.PressAsync("Enter");
        var row = bar.GetByTestId("rule-row");
        await Expect(row.GetByTestId("rule-matches")).ToHaveTextAsync("3");
        await Expect(bar.GetByTestId("rules-preview-count")).ToHaveCountAsync(0);

        await bar.GetByTestId("rules-apply").ClickAsync();
        await Expect(bar.GetByTestId("rules-status")).ToContainTextAsync("3 bookings categorized");
        await Expect(bar.GetByTestId("rules-apply")).ToHaveCountAsync(0);

        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.Name == "Example Grocer").ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows.All(t => t.CategoryId == 200 && t.CategorySetBy == TransactionCategorySource.Rule), Is.True);
        Assert.That(await db.CategoryRules.CountAsync(), Is.EqualTo(1));

        // The category's list now shows them.
        await Expect(Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Grocer" })).ToHaveCountAsync(3);
    }

    [Test]
    public async Task The_expression_is_edited_as_text_and_the_inputs_follow_it()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=cat-200&rules=1");
        var bar = Page.GetByTestId("rules-bar");
        await bar.GetByTestId("rules-add").ClickAsync();
        var expression = bar.GetByTestId("rule-expression");

        // The inputs compose the expression ...
        await bar.GetByTestId("rule-payee").FillAsync("example grocer");
        await Expect(expression).ToHaveValueAsync("name:\"example grocer\"");

        // ... a richer expression is text-only, with the live preview ...
        await expression.FillAsync("grocer AND (amount<-26 OR amount>0)");
        await Expect(bar.GetByTestId("rule-text-only")).ToBeVisibleAsync();
        await Expect(bar.GetByTestId("rule-payee")).ToHaveCountAsync(0);
        await Expect(bar.GetByTestId("rules-preview-count")).ToHaveTextAsync("1 booking matches");

        // ... an invalid one says so and cannot be saved ...
        await expression.FillAsync("grocer AND (");
        await Expect(bar.GetByTestId("rule-syntax-error")).ToBeVisibleAsync();
        await bar.GetByTestId("rule-save").ClickAsync();
        await Expect(bar.GetByTestId("rule-error")).ToBeVisibleAsync();

        // ... and a simple one brings the inputs back.
        await expression.FillAsync("name:\"Example Grocer\" AND amount<0");
        await Expect(bar.GetByTestId("rule-payee")).ToHaveValueAsync("Example Grocer");
        await bar.GetByTestId("rule-save").ClickAsync();
        await Expect(bar.GetByTestId("rule-matches")).ToHaveTextAsync("3");

        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.CategoryRules.SingleAsync()).Expression, Is.EqualTo("name:\"Example Grocer\" AND amount<0"));
    }

    [Test]
    public async Task The_bar_stays_usable_at_phone_width()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=cat-200&rules=1");
        var bar = Page.GetByTestId("rules-bar");
        await bar.GetByTestId("rules-add").ClickAsync();
        await bar.GetByTestId("rule-more").ClickAsync();
        await Expect(bar.GetByTestId("rule-min")).ToBeVisibleAsync();
        await bar.GetByTestId("rule-payee").FillAsync("Example Grocer");
        await bar.GetByTestId("rule-save").ClickAsync();
        await Expect(bar.GetByTestId("rule-matches")).ToHaveTextAsync("3");

        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        Assert.That(overflow, Is.False);
    }
}
