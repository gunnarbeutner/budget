using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Structure editing through the sidebar's context menus (no buttons on the rows): inline rename, colour, move up /
/// down (Alt+Arrow), Move to group…, drag and drop, account settings, deletes (an account only after typing its
/// name), MoneyMoney-owned rows disabled with the reason, and no editing entries for viewers. Uses the editing
/// world (<see cref="EditingSeed"/>); its source never got a MoneyMoney upload, so its structure is OpenBudget's.
/// </summary>
[TestFixture]
public sealed class StructureMenuTests : EndToEndTestBase
{
    private static string BrowseUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3";
    private static readonly long BanksGroupId = RowIds.Namespace(E2ESeed.IdSpace, 7);

    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private ILocator Sidebar => Page.GetByTestId("project-sidebar");
    private ILocator Category(string name) => Sidebar.GetByTestId("tree-category").Filter(new() { HasText = name }).First;
    private ILocator Account(string name) => Sidebar.GetByTestId("tree-account").Filter(new() { HasText = name }).First;

    private async Task OpenMenuAsync(ILocator row)
    {
        await row.ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("context-menu")).ToBeVisibleAsync();
    }

    private async Task GroupGiroInBanksAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        db.AccountGroups.Add(new AccountGroup { Id = BanksGroupId, OwnerUserId = E2ESeed.OwnerUserId, Name = "Banks", Sort = 0 });
        await db.SaveChangesAsync();
        var giro = await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId);
        giro.GroupId = BanksGroupId;
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task A_category_is_renamed_inline_and_recoloured_from_its_menu()
    {
        await GoToAsync(BrowseUrl);
        await OpenMenuAsync(Category("Groceries"));
        await Page.GetByTestId("ctx-rename").ClickAsync();
        var field = Sidebar.GetByTestId("tree-rename");
        await Expect(field).ToBeFocusedAsync();
        await field.FillAsync("Example Market");
        await field.PressAsync("Enter");
        await Expect(Category("Example Market")).ToBeVisibleAsync();
        await Expect(Sidebar.GetByTestId("tree-rename")).ToHaveCountAsync(0);

        // Esc cancels a rename.
        await OpenMenuAsync(Category("Example Market"));
        await Page.GetByTestId("ctx-rename").ClickAsync();
        await Sidebar.GetByTestId("tree-rename").FillAsync("Something else");
        await Sidebar.GetByTestId("tree-rename").PressAsync("Escape");
        await Expect(Category("Example Market")).ToBeVisibleAsync();

        await OpenMenuAsync(Category("Example Market"));
        await Page.GetByTestId("ctx-color").ClickAsync();
        await Page.GetByTestId("color-popover").GetByTestId("color-swatch").Nth(2).ClickAsync();
        await Expect(Page.GetByTestId("color-popover")).ToHaveCountAsync(0);

        await using var db = await Db.CreateDbContextAsync();
        var category = await db.Categories.SingleAsync(c => c.Id == 200);
        Assert.That(category.Name, Is.EqualTo("Example Market"));
        Assert.That(category.Color, Is.EqualTo("#856404"));
    }

    [Test]
    public async Task The_keyboard_moves_a_category_and_creates_one_below_it()
    {
        await GoToAsync(BrowseUrl);
        // Living > Energy, Groceries, Salary. Alt+Arrow Down on the focused row moves it below its next sibling.
        await Category("Groceries").FocusAsync();
        await Page.Keyboard.PressAsync("Alt+ArrowDown");
        await Expect(Sidebar.GetByTestId("tree-category").Nth(3)).ToContainTextAsync("Groceries");

        // Shift+F10 opens the menu; "New category below" starts an inline rename of the new row.
        await Category("Groceries").FocusAsync();
        await Page.Keyboard.PressAsync("Shift+F10");
        await Page.GetByTestId("ctx-new-category").ClickAsync();
        var field = Sidebar.GetByTestId("tree-rename");
        await Expect(field).ToBeFocusedAsync();
        await field.FillAsync("Example Pharmacy");
        await field.PressAsync("Enter");
        await Expect(Category("Example Pharmacy")).ToBeVisibleAsync();

        await using var db = await Db.CreateDbContextAsync();
        var order = await db.Categories.Where(c => c.GroupId == 100).OrderBy(c => c.Sort).Select(c => c.Name).ToListAsync();
        Assert.That(order, Is.EqualTo(new[] { "Energy", "Salary", "Groceries", "Example Pharmacy" }));
    }

    [Test]
    public async Task Deleting_a_category_uncategorizes_its_bookings_but_a_full_group_cannot_be_deleted()
    {
        await GoToAsync(BrowseUrl);
        await OpenMenuAsync(Sidebar.GetByTestId("tree-category").Filter(new() { HasText = "Living" }).First);
        await Expect(Page.GetByTestId("ctx-delete")).ToBeDisabledAsync();
        await Expect(Page.GetByTestId("ctx-delete")).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("Only empty groups"));
        await Page.Keyboard.PressAsync("Escape");

        await OpenMenuAsync(Category("Salary"));
        await Page.GetByTestId("ctx-delete").ClickAsync();
        await Expect(Page.GetByTestId("structure-delete")).ToContainTextAsync("uncategorized");
        await Page.GetByTestId("structure-delete-confirm").ClickAsync();
        await Expect(Sidebar.GetByTestId("tree-category").Filter(new() { HasText = "Salary" })).ToHaveCountAsync(0);

        await using var db = await Db.CreateDbContextAsync();
        Assert.That(await db.Categories.AnyAsync(c => c.Id == 201), Is.False);
        Assert.That((await db.Transactions.SingleAsync(t => t.Id == 104)).CategoryId, Is.Null);
    }

    [Test]
    public async Task Deleting_an_account_needs_its_exact_name_and_takes_its_bookings()
    {
        await GoToAsync(BrowseUrl);
        await OpenMenuAsync(Account(EditingSeed.BankAccountName));
        await Page.GetByTestId("ctx-delete").ClickAsync();

        await Expect(Page.GetByTestId("delete-account-counts")).ToContainTextAsync("4 bookings and 0 statements");
        var confirm = Page.GetByTestId("structure-delete-confirm");
        await Expect(confirm).ToBeDisabledAsync();
        var name = Page.GetByTestId("delete-account-name");
        await name.FillAsync(EditingSeed.BankAccountName.ToUpperInvariant());
        await Expect(confirm).ToBeDisabledAsync();
        await name.FillAsync(EditingSeed.BankAccountName);
        await Expect(confirm).ToBeEnabledAsync();
        await confirm.ClickAsync();

        await Expect(Sidebar.GetByTestId("tree-account").Filter(new() { HasText = EditingSeed.BankAccountName })).ToHaveCountAsync(0);
        await using var db = await Db.CreateDbContextAsync();
        Assert.That(await db.Accounts.AnyAsync(a => a.Id == EditingSeed.BankAccountId), Is.False);
        Assert.That(await db.Transactions.AnyAsync(t => t.AccountId == EditingSeed.BankAccountId), Is.False);
        Assert.That(await db.Transactions.AnyAsync(t => t.AccountId == EditingSeed.MoneyMoneyAccountId), Is.True);
    }

    [Test]
    public async Task Accounts_move_into_a_group_from_the_menu_and_by_drag_and_drop()
    {
        await GroupGiroInBanksAsync();
        await GoToAsync(BrowseUrl);

        await OpenMenuAsync(Account(EditingSeed.MoneyMoneyAccountName));
        await Page.GetByTestId("ctx-move-to").ClickAsync();
        await Page.GetByTestId("move-groups").GetByTestId("move-group").Filter(new() { HasText = "Banks" }).ClickAsync();
        await Expect(Sidebar.GetByTestId("tree-group").Filter(new() { HasText = "Other accounts" })).ToHaveCountAsync(0);
        await using (var db = await Db.CreateDbContextAsync())
            Assert.That((await db.Accounts.SingleAsync(a => a.Id == EditingSeed.MoneyMoneyAccountId)).GroupId, Is.EqualTo(BanksGroupId));

        // Drag the MoneyMoney account onto the Giro: it lands right before it.
        await Account(EditingSeed.MoneyMoneyAccountName).DragToAsync(Account(EditingSeed.BankAccountName));
        await Expect(Sidebar.GetByTestId("tree-account").First).ToContainTextAsync(EditingSeed.MoneyMoneyAccountName);
        await using var after = await Db.CreateDbContextAsync();
        var sorts = await after.Accounts.Where(a => a.GroupId == BanksGroupId).OrderBy(a => a.Sort).Select(a => a.Id).ToListAsync();
        Assert.That(sorts, Is.EqualTo(new[] { EditingSeed.MoneyMoneyAccountId, EditingSeed.BankAccountId }));
    }

    [Test]
    public async Task Deleting_an_account_group_moves_its_accounts_up_and_settings_rename_an_account()
    {
        await GroupGiroInBanksAsync();
        await GoToAsync(BrowseUrl);

        await OpenMenuAsync(Sidebar.GetByTestId("tree-group").Filter(new() { HasText = "Banks" }).First);
        await Page.GetByTestId("ctx-delete").ClickAsync();
        await Expect(Page.GetByTestId("structure-delete")).ToContainTextAsync("move up one level");
        await Page.GetByTestId("structure-delete-confirm").ClickAsync();
        await Expect(Sidebar.GetByTestId("tree-group").Filter(new() { HasText = "Banks" })).ToHaveCountAsync(0);
        await Expect(Account(EditingSeed.BankAccountName)).ToBeVisibleAsync();

        await OpenMenuAsync(Account(EditingSeed.BankAccountName));
        await Page.GetByTestId("ctx-settings").ClickAsync();
        var dialog = Page.GetByTestId("account-settings");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByTestId("account-settings-name").FillAsync("Example Household Giro");
        await Page.GetByTestId("account-settings-save").ClickAsync();
        await Expect(Account("Example Household Giro")).ToBeVisibleAsync();

        await using var db = await Db.CreateDbContextAsync();
        Assert.That(await db.AccountGroups.AnyAsync(g => g.Id == BanksGroupId), Is.False);
        Assert.That((await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId)).GroupId, Is.Null);
    }

    [Test]
    public async Task Viewers_get_no_editing_entries()
    {
        await using (var db = await Db.CreateDbContextAsync())
        {
            var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
            db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Viewer });
            await db.SaveChangesAsync();
        }
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync(BrowseUrl);
        await Expect(Category("Groceries")).ToBeVisibleAsync();
        await Category("Groceries").ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("context-menu")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Account_settings_pick_an_icon_and_colour_and_group_settings_change_the_folder()
    {
        await GroupGiroInBanksAsync();
        await GoToAsync(BrowseUrl);

        await OpenMenuAsync(Account(EditingSeed.BankAccountName));
        await Page.GetByTestId("ctx-settings").ClickAsync();
        var dialog = Page.GetByTestId("account-settings");
        await dialog.Locator("[data-testid=icon-choice][data-icon=bi-house]").ClickAsync();
        await dialog.GetByTestId("icon-color-swatch").Nth(6).ClickAsync();
        await Page.GetByTestId("account-settings-save").ClickAsync();
        await Expect(Account(EditingSeed.BankAccountName).GetByTestId("tree-account-icon")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("bi-house"));

        var group = Sidebar.GetByTestId("tree-group").Filter(new() { HasText = "Banks" }).First;
        await Expect(group.GetByTestId("tree-group-icon")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("bi-folder"));
        await OpenMenuAsync(group);
        await Page.GetByTestId("ctx-settings").ClickAsync();
        await Page.GetByTestId("group-settings").Locator("[data-testid=icon-choice][data-icon=bi-safe]").ClickAsync();
        await Page.GetByTestId("group-settings-save").ClickAsync();
        await Expect(group.GetByTestId("tree-group-icon")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("bi-safe"));

        await using var db = await Db.CreateDbContextAsync();
        var account = await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId);
        Assert.That((account.Icon, account.Color), Is.EqualTo(("bi-house", (string?)"#1d4ed8")));
        Assert.That((await db.AccountGroups.SingleAsync(g => g.Id == BanksGroupId)).Icon, Is.EqualTo("bi-safe"));
    }

    [Test]
    public async Task A_category_group_folds_with_its_chevron_and_stays_folded_after_a_reload()
    {
        await GoToAsync(BrowseUrl);
        var living = Sidebar.GetByTestId("tree-category").Filter(new() { HasText = "Living" }).First;
        await Expect(Category("Groceries")).ToBeVisibleAsync();
        await Expect(living).ToHaveAttributeAsync("aria-expanded", "true");

        await living.GetByTestId("tree-category-group-toggle").ClickAsync();
        await Expect(living).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(Sidebar.GetByTestId("tree-category").Filter(new() { HasText = "Groceries" })).ToHaveCountAsync(0);
        // Folding does not select the group.
        await Expect(living).ToHaveAttributeAsync("aria-selected", "false");

        await Page.ReloadAsync();
        await Expect(living).ToHaveAttributeAsync("aria-expanded", "false");

        // ArrowRight on the focused group unfolds it again.
        await living.FocusAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(Category("Groceries")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Selecting_a_category_keeps_it_indented_like_its_siblings()
    {
        await GoToAsync(BrowseUrl);
        async Task<double> LabelLeftAsync(string name) =>
            (await Category(name).Locator(".browse-tree-label").BoundingBoxAsync())!.X;
        foreach (var (selected, sibling) in new[] { ("Groceries", "Salary"), ("Salary", "Groceries") })
        {
            await Category(selected).ClickAsync();
            await Expect(Category(selected)).ToHaveAttributeAsync("aria-selected", "true");
            Assert.That(await LabelLeftAsync(selected), Is.EqualTo(await LabelLeftAsync(sibling)).Within(0.5), selected);
        }
    }

    [Test]
    public async Task A_group_gets_a_colour_from_its_menu_and_loses_it_again()
    {
        await GoToAsync(BrowseUrl);
        var living = Category("Living");
        await OpenMenuAsync(living);
        await Page.GetByTestId("ctx-color").ClickAsync();
        await Page.GetByTestId("color-popover").GetByTestId("color-swatch").Nth(4).ClickAsync();
        await Expect(living.Locator("svg.browse-ribbon")).ToHaveCountAsync(1);

        await OpenMenuAsync(living);
        await Page.GetByTestId("ctx-color").ClickAsync();
        await Expect(Page.GetByTestId("color-reset")).ToHaveTextAsync("No colour");
        await Page.GetByTestId("color-reset").ClickAsync();
        await Expect(living.Locator("i.bi-folder")).ToHaveCountAsync(1);
        await Expect(living.Locator("svg.browse-ribbon")).ToHaveCountAsync(0);
    }
}
