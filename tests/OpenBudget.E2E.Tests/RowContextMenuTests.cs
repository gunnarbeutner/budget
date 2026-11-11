using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The transaction row's context menu: it stays inside the window when opened at the bottom-right corner, it opens
/// from the expanded details too, and its "Create offsetting entry" submenu books the opposite amount on a manual account.
/// </summary>
[TestFixture]
public sealed class RowContextMenuTests : EndToEndTestBase
{
    private static readonly long CashId = SourceScope.Namespace(E2ESeed.SourceId, 7);
    private const string CashName = "Example Cash";

    private static string GiroUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}";

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    [SetUp]
    public async Task SeedWorld()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        db.Accounts.Add(new Account
        {
            Id = CashId,
            SourceId = E2ESeed.SourceId,
            Name = CashName,
            Currency = "EUR",
            Type = 6,
            Provider = AccountProviders.Manual,
            Balances = "11:100.000000,3:EUR,",
        });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = CashId });
        (await db.Transactions.SingleAsync(t => t.Id == 101)).CategoryId = 202;
        await db.SaveChangesAsync();
    }

    private ILocator Row(string text) => Page.GetByTestId("tx-row").Filter(new() { HasText = text });

    [Test]
    public async Task The_menu_opened_at_the_bottom_right_corner_stays_inside_the_window()
    {
        await Page.SetViewportSizeAsync(900, 520);
        await GoToAsync(GiroUrl);
        await Expect(Row("−25,00")).ToBeVisibleAsync();

        // A right-click at the very corner (as on a row near the window's bottom-right edge).
        await Row("−25,00").DispatchEventAsync("contextmenu", new { bubbles = true, cancelable = true, clientX = 896, clientY = 516, button = 2 });
        var menu = Page.GetByTestId("context-menu");
        await Expect(menu).ToBeVisibleAsync();
        var box = (await menu.BoundingBoxAsync())!;
        Assert.That(box.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(box.Y, Is.GreaterThanOrEqualTo(0));
        Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(900));
        Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(520));
        // Flipped above / left of the pointer rather than squeezed.
        Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(897));
        Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(517));

        // Its last entry can be reached (scrolling inside the menu when the window is short).
        var last = menu.GetByTestId("ctx-split");
        await last.ScrollIntoViewIfNeededAsync();
        var lastBox = (await last.BoundingBoxAsync())!;
        Assert.That(lastBox.Y + lastBox.Height, Is.LessThanOrEqualTo(520));

        // The submenu also fits.
        await menu.GetByTestId("ctx-offset").HoverAsync();
        var sub = Page.GetByTestId("context-submenu");
        await Expect(sub).ToBeVisibleAsync();
        var subBox = (await sub.BoundingBoxAsync())!;
        Assert.That(subBox.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(subBox.X + subBox.Width, Is.LessThanOrEqualTo(900));
        Assert.That(subBox.Y + subBox.Height, Is.LessThanOrEqualTo(520));
    }

    [Test]
    public async Task A_right_click_in_the_expanded_details_opens_the_row_menu_but_not_in_the_note_field()
    {
        await GoToAsync(GiroUrl);
        await Row("−25,00").ClickAsync();
        var details = Page.GetByTestId("tx-details");
        await Expect(details).ToBeVisibleAsync();

        await details.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = 10, Y = 10 } });
        await Expect(Page.GetByTestId("context-menu")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ctx-search-payee")).ToBeEnabledAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("context-menu")).ToHaveCountAsync(0);

        await Page.GetByTestId("tx-details-note").DispatchEventAsync("contextmenu", new { bubbles = true, cancelable = true, button = 2 });
        await Expect(Page.GetByTestId("context-menu")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task An_offsetting_entry_is_created_from_the_row_menu_on_a_manual_account()
    {
        await GoToAsync(GiroUrl);
        await Row("−25,00").ClickAsync(new() { Button = MouseButton.Right });
        var menu = Page.GetByTestId("context-menu");
        await menu.GetByTestId("ctx-offset").ClickAsync();
        var target = Page.GetByTestId("context-submenu").GetByTestId($"ctx-offset-{CashId}");
        await Expect(target).ToContainTextAsync(CashName);
        await target.ClickAsync();

        var toast = Page.GetByTestId("toast").Filter(new() { HasText = CashName });
        await Expect(toast).ToBeVisibleAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var entry = await db.Transactions.AsNoTracking().SingleAsync(t => t.AccountId == CashId);
            Assert.That(entry.Amount, Is.EqualTo(25_00));
            Assert.That(entry.Name, Is.EqualTo("Example Grocer"));
            Assert.That(entry.CategoryId, Is.EqualTo(202));
            Assert.That(entry.Note, Does.Contain("Example Grocer"));
            var cash = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == CashId);
            Assert.That(cash.CurrentBalance, Is.EqualTo(125m));
        }

        // The toast leads to the new booking.
        await toast.GetByTestId("toast-show-booking").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"sel=acct-{CashId}"));
        await Expect(Row("25,00")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.GetByTestId("tx-details")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_details_offer_the_offsetting_entry_too()
    {
        await GoToAsync(GiroUrl);
        await Row("−30,00").ClickAsync();
        await Page.GetByTestId("tx-details-offset").ClickAsync();
        await Page.GetByTestId($"tx-details-offset-{CashId}").ClickAsync();
        await Expect(Page.GetByTestId("toast").Filter(new() { HasText = CashName })).ToBeVisibleAsync();
        await using var db = await Db.CreateDbContextAsync();
        Assert.That((await db.Transactions.AsNoTracking().SingleAsync(t => t.AccountId == CashId)).Amount, Is.EqualTo(30_00));
    }
}
