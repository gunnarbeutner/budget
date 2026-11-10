using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The editable transaction list: select and expand a row, categorize through the picker with the keyboard, bulk edits via the
/// context menu with undo, the filter bar, the read-only lock on a MoneyMoney row, and a viewer who may not edit.
/// The extra world (see <see cref="EditingSeed"/>) has a connector account with uncategorized "Example Grocer" rows.
/// </summary>
[TestFixture]
public sealed class TransactionEditingTests : EndToEndTestBase
{
    private static string UncategorizedUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized";

    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>());

    // Cmd on macOS (Ctrl-click is a right click there), Ctrl elsewhere; the page accepts either.
    private static KeyboardModifier CommandKey => OperatingSystem.IsMacOS() ? KeyboardModifier.Meta : KeyboardModifier.Control;

    private ILocator Row(string text) => Page.GetByTestId("tx-row").Filter(new() { HasText = text });

    private static async Task<Transaction> LoadAsync(long id)
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task PickInOpenPickerAsync(string search)
    {
        var searchBox = Page.GetByTestId("category-picker-search");
        await Expect(searchBox).ToBeFocusedAsync();
        await searchBox.FillAsync(search);
        await Expect(Page.GetByTestId("category-picker-option").First).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.GetByTestId("category-picker")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Row_text_can_be_selected_without_expanding_the_row()
    {
        await GoToAsync(UncategorizedUrl);
        var payee = Row("\u221225,00").GetByText("Example Grocer").First;
        var box = (await payee.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + 1, box.Y + box.Height / 2);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(box.X + box.Width - 1, box.Y + box.Height / 2, new() { Steps = 5 });
        await Page.Mouse.UpAsync();

        var selected = await Page.EvaluateAsync<string>("() => window.getSelection().toString()");
        Assert.That(selected, Does.Contain("Example"));
        await Expect(Page.GetByTestId("tx-details")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Clicking_a_row_selects_it_and_expands_the_bank_details_in_place()
    {
        await GoToAsync(UncategorizedUrl);
        await Expect(Page.GetByText(new Regex("4 transactions"))).ToBeVisibleAsync();

        await Row("\u221225,00").ClickAsync();

        var details = Page.GetByTestId("tx-details");
        await Expect(details).ToBeVisibleAsync();
        await Expect(Row("\u221225,00")).ToContainTextAsync("Example Grocer");                    // payee stays in the row above the details
        await Expect(details).ToContainTextAsync("DE89 3704 0044 0532 0130 00");
        await Expect(details).ToContainTextAsync("COBADEFFXXX");
        await Expect(details).ToContainTextAsync("Card payment 1234");
        await Expect(details).ToContainTextAsync("Lastschrift");
        await Expect(details).ToContainTextAsync("REF-101");
        await Expect(details).ToContainTextAsync(EditingSeed.BankAccountName);
        await Expect(Page.GetByTestId("action-bar-count")).ToContainTextAsync("1 selected");

        // Selecting another row moves the expansion; clicking the expanded row again folds it.
        await Row("\u221230,00").ClickAsync();
        await Expect(details).ToHaveCountAsync(1);
        await Expect(details).ToContainTextAsync("REF-102");
        await Row("\u221230,00").ClickAsync();
        await Expect(details).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Categorizing_with_the_keyboard_marks_it_manual_offers_similar_rows_and_undoes()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync();

        await Page.Keyboard.PressAsync("c");
        await Expect(Page.GetByTestId("category-picker")).ToBeVisibleAsync();
        await PickInOpenPickerAsync("gro");

        var toast = Page.GetByTestId("toast").First;
        await Expect(toast).ToContainTextAsync("Category set on 1 transaction");
        await Expect(Page.GetByTestId("toast-similar")).ToContainTextAsync("Also categorize 2 similar");
        // The row left the Uncategorized list.
        await Expect(Page.GetByText(new Regex("3 transactions"))).ToBeVisibleAsync();
        var row = await LoadAsync(101);
        Assert.That(row.CategoryId, Is.EqualTo(200));
        Assert.That(row.CategorySetBy, Is.EqualTo(TransactionCategorySource.Manual));

        await Page.GetByTestId("toast-undo").First.ClickAsync();
        await Expect(Page.GetByText(new Regex("4 transactions"))).ToBeVisibleAsync();
        var undone = await LoadAsync(101);
        Assert.That(undone.CategoryId, Is.Null);
        Assert.That(undone.CategorySetBy, Is.EqualTo(TransactionCategorySource.Import));
    }

    [Test]
    public async Task Also_categorize_similar_offers_the_same_payees_other_rows()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync();
        await Page.Keyboard.PressAsync("c");
        await PickInOpenPickerAsync("gro");

        await Page.GetByTestId("toast-similar").ClickAsync();

        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();   // only the MoneyMoney bakery row is left
        foreach (var id in new long[] { 101, 102, 103 }) Assert.That((await LoadAsync(id)).CategoryId, Is.EqualTo(200));
        Assert.That((await LoadAsync(110)).CategoryId, Is.Null);

        // The account itself carries one read-only notice in the toolbar instead of a lock per row.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.MoneyMoneyAccountId}");
        await Expect(Page.GetByTestId("browse-readonly")).ToHaveAttributeAsync("title", "Categorized in MoneyMoney until this account switches to a bank connector");
        await Expect(Page.GetByTestId("tx-check")).ToBeDisabledAsync();
    }

    [Test]
    public async Task Bulk_categorizing_through_the_context_menu_is_one_undo_step()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync();
        await Row("\u221212,50").ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        // Newest first: 12.50, 30.00, bakery 4.50, 25.00 - the range covers all four.
        await Expect(Page.GetByTestId("action-bar-count")).ToContainTextAsync("4 selected");
        await Expect(Page.GetByTestId("action-bar-count")).ToContainTextAsync("\u221272,00\u00A0€");

        // Ctrl-click removes rows from the selection again.
        await Row("\u221230,00").ClickAsync(new() { Modifiers = [CommandKey] });
        await Row("\u22124,50").ClickAsync(new() { Modifiers = [CommandKey] });
        await Expect(Page.GetByTestId("action-bar-count")).ToContainTextAsync("2 selected");

        await Row("\u221225,00").ClickAsync(new() { Button = MouseButton.Right });
        var menu = Page.GetByTestId("context-menu");
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.GetByTestId("ctx-search-payee")).ToHaveCountAsync(0);       // multi-selection: bulk-safe entries only
        await menu.GetByTestId("ctx-category").ClickAsync();
        await PickInOpenPickerAsync("energy");

        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("Category set on 2 transactions");
        Assert.That((await LoadAsync(101)).CategoryId, Is.EqualTo(202));
        Assert.That((await LoadAsync(103)).CategoryId, Is.EqualTo(202));
        Assert.That((await LoadAsync(102)).CategoryId, Is.Null);

        await Page.GetByTestId("toast-undo").First.ClickAsync();
        await Expect(Page.GetByText(new Regex("4 transactions"))).ToBeVisibleAsync();
        Assert.That((await LoadAsync(101)).CategoryId, Is.Null);
        Assert.That((await LoadAsync(103)).CategoryId, Is.Null);
    }

    [Test]
    public async Task The_single_row_context_menu_has_the_full_set_and_search_for_this_payee_prefills_the_search()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync(new() { Button = MouseButton.Right });

        var menu = Page.GetByTestId("context-menu");
        foreach (var key in new[] { "category", "remove-category", "note", "check", "search-payee", "similar", "go-account", "rule", "split", "transfer" })
            await Expect(menu.GetByTestId("ctx-" + key)).ToBeVisibleAsync();
        await Expect(menu.GetByTestId("ctx-rule")).ToBeDisabledAsync();
        await Expect(menu.GetByTestId("ctx-go-category")).ToBeDisabledAsync();            // uncategorized: nothing to go to

        await menu.GetByTestId("ctx-search-payee").ClickAsync();
        await Expect(Page.GetByTestId("tx-search")).ToHaveValueAsync("Example Grocer");
        await Expect(Page.GetByText(new Regex("3 transactions"))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Notes_and_check_marks_are_edited_in_place_and_by_key()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync();

        var note = Page.GetByTestId("tx-details-note");
        await note.FillAsync("for the tax return");
        await note.PressAsync("Tab");
        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("Note changed on 1 transaction");
        await Expect(Row("\u221225,00").GetByTestId("tx-note")).ToContainTextAsync("for the tax return");
        Assert.That((await LoadAsync(101)).Note, Is.EqualTo("for the tax return"));

        await Page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });   // focus off the input, rows keep their selection
        await Page.Keyboard.PressAsync("x");
        await Expect(Row("\u221225,00").GetByTestId("tx-check")).ToBeCheckedAsync();
        Assert.That((await LoadAsync(101)).Checked, Is.True);
    }

    [Test]
    public async Task Typing_in_inputs_is_not_hijacked_by_the_shortcuts_and_the_overlay_lists_them()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221225,00").ClickAsync();
        var search = Page.GetByTestId("tx-search");
        await search.ClickAsync();
        await search.PressSequentiallyAsync("cnx?/", new() { Delay = 150 });
        await Expect(search).ToHaveValueAsync("cnx?/");
        await Expect(Page.GetByTestId("category-picker")).ToHaveCountAsync(0);

        await search.FillAsync("");
        await Page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await Page.Keyboard.PressAsync("?");
        var list = Page.GetByTestId("shortcuts-list");
        await Expect(list).ToBeVisibleAsync();
        await Expect(list).ToContainTextAsync("Set the category");
        await Page.Keyboard.PressAsync("Escape");
    }

    [Test]
    public async Task Arrow_keys_move_the_selection_and_ctrl_a_selects_everything_in_the_filter()
    {
        await GoToAsync(UncategorizedUrl);
        await Row("\u221230,00").ClickAsync();
        await Page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });

        await Page.Keyboard.PressAsync("ArrowDown");
        await Expect(Page.GetByTestId("tx-details")).ToContainTextAsync("REF-110");   // newest first: 103, 102, then the bakery (110), then 101
        await Page.Keyboard.PressAsync(OperatingSystem.IsMacOS() ? "Meta+a" : "Control+a");
        await Expect(Page.GetByTestId("action-bar-count")).ToContainTextAsync("4 selected");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("action-bar")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_filter_bar_narrows_by_check_mark_new_direction_and_period()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        // "All transactions" shows the categorized rows of tracked categories: the baseline pair plus the salary.
        await Expect(Page.GetByText(new Regex("3 transactions"))).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("filter-bar")).ToHaveCountAsync(0);                             // the panel opens from the toolbar
        await Page.GetByTestId("filter-toggle").ClickAsync();
        await Page.GetByTestId("filter-direction").SelectOptionAsync("in");
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();                // baseline salary + employer
        await Expect(Page.GetByTestId("filter-count")).ToHaveTextAsync("1");
        await Page.GetByTestId("filter-direction").SelectOptionAsync("any");

        await Page.GotoAsync(UncategorizedUrl);
        await Expect(Page.GetByText(new Regex("4 transactions"))).ToBeVisibleAsync();
        await Page.GetByTestId("filter-toggle").ClickAsync();
        await Expect(Row("\u221230,00").GetByTestId("tx-new-dot")).ToBeVisibleAsync();

        await Page.GetByTestId("filter-new").ClickAsync();
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Expect(Row("\u221230,00")).ToBeVisibleAsync();
        await Page.GetByTestId("filter-new").ClickAsync();

        await Row("\u221225,00").ClickAsync();
        await Page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await Page.Keyboard.PressAsync("x");
        await Expect(Row("\u221225,00").GetByTestId("tx-check")).ToBeCheckedAsync();
        await Page.GetByTestId("filter-checked").SelectOptionAsync("yes");
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Page.GetByTestId("filter-checked").SelectOptionAsync("no");
        await Expect(Page.GetByText(new Regex("3 transactions"))).ToBeVisibleAsync();
        await Page.GetByTestId("filter-checked").SelectOptionAsync("any");

        await Page.GetByTestId("filter-period").SelectOptionAsync("custom");
        await Page.GetByTestId("filter-from").FillAsync("2026-03-11");
        await Page.GetByTestId("filter-to").FillAsync("2026-03-12");
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();          // the 12th (Grocer) and the 11th (Bakery)
    }

    [Test]
    public async Task Mark_all_as_checked_covers_the_filter_and_can_be_undone()
    {
        await GoToAsync(UncategorizedUrl);
        await Page.GetByTestId("mark-all-checked").ClickAsync();
        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("marked as checked");
        Assert.That((await LoadAsync(101)).Checked, Is.True);
        Assert.That((await LoadAsync(110)).Checked, Is.False);                  // MoneyMoney row: read-only, skipped
        await Page.GetByTestId("toast-undo").First.ClickAsync();
        await Expect(Row("\u221225,00").GetByTestId("tx-check")).Not.ToBeCheckedAsync();
        Assert.That((await LoadAsync(101)).Checked, Is.False);
    }

    [Test]
    public async Task Viewing_a_row_clears_its_new_dot()
    {
        await GoToAsync(UncategorizedUrl);
        await Expect(Row("\u221230,00").GetByTestId("tx-new-dot")).ToBeVisibleAsync();
        await Row("\u221230,00").ClickAsync();
        await Expect(Row("\u221230,00").GetByTestId("tx-new-dot")).ToHaveCountAsync(0);
        for (var i = 0; i < 40 && (await LoadAsync(102)).IsNew; i++) await Task.Delay(100);
        Assert.That((await LoadAsync(102)).IsNew, Is.False);
    }

    [Test]
    public async Task A_moneymoney_row_is_locked_read_only()
    {
        await GoToAsync(UncategorizedUrl);
        var bakery = Row("\u22124,50");
        var check = bakery.GetByTestId("tx-check");
        await Expect(check).ToBeDisabledAsync();
        await Expect(check).ToHaveAttributeAsync("title", "Categorized in MoneyMoney until this account switches to a bank connector");
        await Expect(bakery.GetByTestId("tx-category")).ToHaveCountAsync(0);          // no inline picker button

        await bakery.ClickAsync();
        await Expect(Page.GetByTestId("tx-details-readonly")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-details-note")).ToHaveCountAsync(0);

        await Page.Keyboard.PressAsync("c");
        await Expect(Page.GetByTestId("category-picker")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("Categorized in MoneyMoney");

        await bakery.ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("ctx-category")).ToBeDisabledAsync();
        Assert.That((await LoadAsync(110)).CategoryId, Is.Null);

        // The account itself carries one read-only notice in the toolbar instead of a lock per row.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.MoneyMoneyAccountId}");
        await Expect(Page.GetByTestId("browse-readonly")).ToHaveAttributeAsync("title", "Categorized in MoneyMoney until this account switches to a bank connector");
        await Expect(Page.GetByTestId("tx-check")).ToBeDisabledAsync();
    }

    [Test]
    public async Task Single_account_views_show_day_separators_with_the_running_balance()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        var days = Page.GetByTestId("tx-day");
        await Expect(days).ToHaveCountAsync(3);                                                   // 14th, 12th, 10th
        // The newest day ends with the current balance (1000.00); earlier days are derived backwards.
        await Expect(Page.GetByTestId("tx-day-balance").First).ToHaveTextAsync("1.000,00\u00A0€");
        await Expect(Page.GetByTestId("tx-day-balance").Nth(1)).ToHaveTextAsync("\u2212987,50\u00A0€");     // 1000 - (2000.00 - 12.50)
        await Expect(Page.GetByTestId("tx-day-balance").Nth(2)).ToHaveTextAsync("\u2212957,50\u00A0€");     // ... - (-30.00)
    }

    [Test]
    public async Task A_project_viewer_cannot_edit()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
            db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Viewer });
            await db.SaveChangesAsync();
        }
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);

        await GoToAsync(UncategorizedUrl);
        await Expect(Row("\u221225,00")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-category")).ToHaveCountAsync(0);
        await Row("\u221225,00").ClickAsync();
        await Expect(Page.GetByTestId("tx-details-readonly")).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("c");
        await Expect(Page.GetByTestId("category-picker")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("only view");
    }

    [Test]
    public async Task An_account_balance_chart_shows_daily_points_across_the_month_at_full_width()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Chart" }).ClickAsync();
        var chart = Page.GetByTestId("browse-chart");
        await Expect(chart).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("chart-granularity").GetByRole(AriaRole.Button, new() { Name = "D" })).ToHaveClassAsync(new Regex("active"));
        var box = await chart.BoundingBoxAsync();
        var main = await Page.Locator(".browse-main").BoundingBoxAsync();
        Assert.That(box!.Width, Is.GreaterThan(main!.Width * 0.8));
        Assert.That(box.Height, Is.GreaterThanOrEqualTo(170));
        var points = await Page.EvaluateAsync<int>("() => Chart.getChart('browseChart').data.labels.length");
        Assert.That(points, Is.EqualTo(31));

        // Monthly history on request, remembered in the URL.
        await Page.GetByTestId("chart-granularity").GetByRole(AriaRole.Button, new() { Name = "M" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("g=M"));
    }
}
