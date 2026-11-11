using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Documents view: the grid of the project's statements with per-user unread dots and the sidebar count,
/// the viewer (opening marks read), filters, "Mark all as read", the "Statement download" settings, the old
/// per-account URL forwarding here, and Documents in the phone bottom bar.
/// </summary>
[TestFixture]
public sealed class DocumentsTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>
    /// The editing world plus a fake-bank login of the owner linked to the giro account, with three statements of
    /// the giro (2026-01..03) and one document of the login without an account.
    /// </summary>
    private async Task SeedDocumentsAsync(bool editingWorld = true)
    {
        if (editingWorld) await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
        var login = new BankLogin
        {
            OwnerUserId = owner.Id,
            ConnectorType = "fake",
            DisplayName = "Example Bank",
            RefreshInterval = RefreshInterval.ManualOnly,
            LastSyncAt = DateTime.UtcNow,
        };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount
        {
            BankLoginId = login.Id,
            RemoteId = "FAKE-GIRO",
            Name = "Giro",
            LinkedAccountId = EditingSeed.BankAccountId,
            StatementsInterval = StatementsInterval.ManualOnly,
            LastSuccessAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var store = Factory.Services.GetRequiredService<BankStatementStore>();
        var account = await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId);
        for (var m = 1; m <= 3; m++)
        {
            var date = new DateOnly(2026, m, 28);
            await store.StoreAsync(db, login, account, new RemoteStatement($"S-{m}", "FAKE-GIRO", date, $"Statement 2026-{m:D2}", Pdf($"Example statement 2026-{m:D2}")), CancellationToken.None);
        }
        await store.StoreAsync(db, login, null, new RemoteStatement("TAX-2025", null, new DateOnly(2026, 2, 1), "Tax certificate 2025", Pdf("Example tax certificate")), CancellationToken.None);
    }

    /// <summary>A minimal valid one-page PDF with a line of text (pdf.js renders it).</summary>
    private static byte[] Pdf(string text, bool landscape = false)
    {
        var stream = $"BT /F1 24 Tf 72 720 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {(landscape ? "842 595" : "595 842")}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private ILocator Cards => Page.GetByTestId("doc-card");
    private ILocator SidebarCount => Page.GetByTestId("view-documents").Locator(".shell-view-badge");

    [Test]
    public async Task The_grid_shows_the_projects_documents_newest_first_with_unread_dots_and_thumbnails()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");

        await Expect(Page.GetByTestId("view-documents")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Cards).ToHaveCountAsync(4);
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(4);
        await Expect(SidebarCount).ToHaveTextAsync("4 new");
        await Expect(Cards.First).ToContainTextAsync(EditingSeed.BankAccountName);
        await Expect(Cards.First).ToContainTextAsync("28.03.2026");
        await Expect(Cards.Nth(2)).ToContainTextAsync("Example Bank");   // the login's own document, by its login name
        await Expect(Cards.First.Locator("img")).ToHaveAttributeAsync("alt", $"Statement {EditingSeed.BankAccountName} 28.03.2026");
        // pdf.js renders the first page into the card.
        await Expect(Cards.First.Locator(".doc-thumb.loaded")).ToHaveCountAsync(1, new() { Timeout = 15000 });
    }

    [Test]
    public async Task Opening_a_document_shows_it_marks_it_read_and_the_sidebar_count_drops()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(SidebarCount).ToHaveTextAsync("4 new");

        await Cards.First.ClickAsync();
        var viewer = Page.GetByTestId("doc-viewer");
        await Expect(viewer).ToBeVisibleAsync();
        await Expect(viewer).ToContainTextAsync("28.03.2026");
        await Expect(viewer.Locator("canvas.doc-page")).ToHaveCountAsync(1, new() { Timeout = 15000 });
        await Expect(viewer.GetByTestId("doc-download")).ToHaveAttributeAsync("href", new Regex(@"/statements/\d+\?download=true$"));
        await Expect(SidebarCount).ToHaveTextAsync("3 new");

        // Arrow right moves to the next (older) document, which is then read too.
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(viewer).ToContainTextAsync("28.02.2026");
        await Expect(SidebarCount).ToHaveTextAsync("2 new");

        // Escape closes and focus returns to that card.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(viewer).ToHaveCountAsync(0);
        await Expect(Cards.Nth(1)).ToBeFocusedAsync();
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(2);

        // Read state survives a reload.
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(2);
    }

    [Test]
    public async Task Filters_narrow_by_status_and_account()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Cards.First.ClickAsync();
        await Expect(SidebarCount).ToHaveTextAsync("3 new");
        await Page.GetByTestId("doc-close").ClickAsync();

        await Page.GetByTestId("docs-status").SelectOptionAsync("unread");
        await Expect(Page).ToHaveURLAsync(new Regex(@"status=unread"));
        await Expect(Cards).ToHaveCountAsync(3);

        await Page.GetByTestId("docs-account").SelectOptionAsync("none");
        await Expect(Cards).ToHaveCountAsync(1);
        await Expect(Cards.First).ToContainTextAsync("Example Bank");

        await Page.GetByTestId("docs-status").SelectOptionAsync("all");
        await Expect(Page).Not.ToHaveURLAsync(new Regex(@"status=unread")); // each filter navigates; let it land first
        await Page.GetByTestId("docs-account").SelectOptionAsync(EditingSeed.BankAccountId.ToString(CultureInfo.InvariantCulture));
        await Expect(Cards).ToHaveCountAsync(3);

        await Page.GetByTestId("docs-search").FillAsync("2026-02");
        await Expect(Cards).ToHaveCountAsync(1);
    }

    [Test]
    public async Task Mark_all_as_read_applies_to_the_current_filter()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents?account={EditingSeed.BankAccountId}");
        await Expect(Cards).ToHaveCountAsync(3);

        await Page.GetByTestId("docs-mark-all").ClickAsync();
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(0);
        await Expect(SidebarCount).ToHaveTextAsync("1 new");   // the login's document outside the filter stays unread
        await Expect(Page.GetByTestId("docs-mark-all")).ToBeDisabledAsync();
    }

    [Test]
    public async Task Settings_switch_the_download_for_a_bank_account_and_explain_why_others_cannot()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Page.GetByTestId("docs-settings").ClickAsync();

        var dialog = Page.GetByTestId("modal-dialog");
        await Expect(dialog).ToContainTextAsync("Statement download");
        await Expect(dialog.GetByTestId("settings-note")).ToHaveTextAsync("If no new statement is there yet, it tries again daily.");

        var mm = dialog.Locator($"[data-testid=setting-row][data-account='{EditingSeed.MoneyMoneyAccountId}']");
        await Expect(mm.GetByTestId("setting-switch")).ToBeDisabledAsync();
        await Expect(mm.GetByTestId("setting-reason")).ToHaveTextAsync("Comes from MoneyMoney");

        var giro = dialog.Locator($"[data-testid=setting-row][data-account='{EditingSeed.BankAccountId}']");
        await Expect(giro.GetByTestId("setting-switch")).Not.ToBeCheckedAsync();
        await giro.GetByTestId("setting-switch").CheckAsync();
        await Expect(giro.GetByTestId("setting-interval")).ToBeVisibleAsync();
        await Expect(giro.GetByTestId("setting-download")).ToBeVisibleAsync();
        await giro.GetByTestId("setting-interval").SelectOptionAsync("Weekly");
        await giro.GetByTestId("setting-download").ClickAsync();
        await Expect(giro.GetByTestId("settings-message")).ToBeVisibleAsync();

        await using var db = await Db.CreateDbContextAsync();
        var link = await db.BankLoginAccounts.SingleAsync(a => a.LinkedAccountId == EditingSeed.BankAccountId);
        Assert.That(link.StatementsEnabled, Is.True);
        Assert.That(link.StatementsInterval, Is.EqualTo(StatementsInterval.Weekly));
    }

    [Test]
    public async Task A_landscape_statement_shows_its_whole_first_page_in_the_card()
    {
        await SeedDocumentsAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var login = await db.BankLogins.SingleAsync();
            var account = await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId);
            var store = Factory.Services.GetRequiredService<BankStatementStore>();
            await store.StoreAsync(db, login, account, new RemoteStatement("S-WIDE", "FAKE-GIRO", new DateOnly(2026, 10, 30), "Cost information", Pdf("Example cost information", landscape: true)), CancellationToken.None);
        }
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Cards.Filter(new() { HasText = "30.10.2026" }).Locator(".doc-thumb")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bwide\b"));
        await Expect(Cards.Filter(new() { HasText = "01.02.2026" }).Locator(".doc-thumb")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bloaded\b"));
        await Expect(Cards.Filter(new() { HasText = "01.02.2026" }).Locator(".doc-thumb")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bwide\b"));
    }

    [Test]
    public async Task Settings_open_when_the_project_has_no_statements_yet()
    {
        await EditingSeed.SeedAsync(Db);
        await GoToAsync($"/projects/{Pid}/documents?settings=1");
        await Expect(Page.GetByTestId("modal-dialog")).ToContainTextAsync("Statement download");
        await Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        await Page.GetByTestId("modal-dialog").GetByTestId("setting-row").First.WaitForAsync();
    }

    [Test]
    public async Task The_gear_menu_opens_the_statement_download_settings()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/browse");
        await Page.GetByTestId("gear-menu").Locator("summary").ClickAsync();
        await Page.GetByTestId("gear-menu").GetByTestId("gear-statement-download").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToContainTextAsync("Statement download");
    }

    [Test]
    public async Task The_old_account_statements_url_opens_the_documents_view_filtered_to_the_account()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/accounts/{EditingSeed.BankAccountId}/statements");
        await Expect(Page).ToHaveURLAsync(new Regex($@"/projects/{Pid}/documents\?account={EditingSeed.BankAccountId}$"));
        await Expect(Cards).ToHaveCountAsync(3);
    }

    [Test]
    public async Task The_transactions_documents_button_opens_the_view_for_the_selected_account()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/browse?sel=acct-{EditingSeed.BankAccountId}");
        await Page.GetByTestId("account-documents").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($@"/projects/{Pid}/documents\?account={EditingSeed.BankAccountId}$"));
        await Expect(Cards).ToHaveCountAsync(3);
    }

    [Test]
    public async Task A_viewer_of_the_shared_project_sees_the_account_statements_but_not_the_owners_login_documents()
    {
        await SeedDocumentsAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
            db.ProjectShares.Add(new ProjectShare { ProjectId = Pid, UserId = guest.Id, Role = ProjectRole.Viewer });
            await db.SaveChangesAsync();
        }
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Cards).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("documents-grid")).Not.ToContainTextAsync("Example Bank");

        await Page.GetByTestId("docs-settings").ClickAsync();
        var giro = Page.GetByTestId("modal-dialog").Locator($"[data-testid=setting-row][data-account='{EditingSeed.BankAccountId}']");
        await Expect(giro.GetByTestId("setting-switch")).ToBeDisabledAsync();
        await Expect(giro.GetByTestId("setting-readonly")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_phone_bottom_bar_shows_documents_with_the_unread_count()
    {
        await SeedDocumentsAsync();
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/browse");
        var bar = Page.GetByTestId("bottom-bar");
        await Expect(bar.GetByTestId("bottom-documents")).ToBeVisibleAsync();
        await Expect(bar.GetByTestId("bottom-documents").GetByTestId("bottom-badge")).ToHaveTextAsync("4");
        await Expect(bar.GetByTestId("bottom-browse")).ToBeVisibleAsync();
        await Expect(bar.GetByTestId("bottom-budgets")).ToBeVisibleAsync();

        await bar.GetByTestId("bottom-documents").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($@"/projects/{Pid}/documents$"));
        await Expect(Cards).ToHaveCountAsync(4);
    }

    private ILocator Checks => Page.GetByTestId("doc-check");

    [Test]
    public async Task Checkboxes_select_cards_and_shift_click_selects_a_range()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Cards).ToHaveCountAsync(4);
        await Expect(Page.GetByTestId("docs-selbar")).ToHaveCountAsync(0);

        await Cards.First.HoverAsync();
        await Checks.First.ClickAsync();
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("1 selected");
        await Expect(Page.GetByTestId("documents-toolbar")).ToBeHiddenAsync();
        await Expect(Checks.First).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(Page.GetByTestId("doc-viewer")).ToHaveCountAsync(0);   // the checkbox does not open the viewer

        await Checks.Nth(2).ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("3 selected");
        await Expect(Cards.Nth(1)).ToHaveAttributeAsync("data-selected", "true");
        await Expect(Cards.Nth(3)).ToHaveAttributeAsync("data-selected", "false");

        // Toggle one off, then select all and clear with Escape; Ctrl+A selects all visible cards.
        await Checks.Nth(1).ClickAsync();
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("2 selected");
        await Page.GetByTestId("docs-sel-all").ClickAsync();
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("4 selected");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("docs-selbar")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("documents-toolbar")).ToBeVisibleAsync();
        await Page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await Page.Keyboard.PressAsync("Control+a");
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("4 selected");

        // Clicking a card body still opens the viewer.
        await Page.GetByTestId("docs-sel-clear").ClickAsync();
        await Cards.First.ClickAsync();
        await Expect(Page.GetByTestId("doc-viewer")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_checkbox_hides_again_after_the_viewer_closes()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Cards.First.ClickAsync();
        await Expect(Page.GetByTestId("doc-viewer")).ToBeVisibleAsync();
        await Page.GetByTestId("doc-close").ClickAsync();
        await Expect(Page.GetByTestId("doc-viewer")).ToHaveCountAsync(0);
        await Page.Mouse.MoveAsync(1, 1);
        await Expect(Checks.First).ToHaveCSSAsync("opacity", "0");
    }

    [Test]
    public async Task Dragging_a_rectangle_over_empty_space_selects_the_cards_it_touches()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Cards).ToHaveCountAsync(4);
        var grid = await Page.GetByTestId("documents-grid").BoundingBoxAsync();
        var first = await Cards.Nth(0).BoundingBoxAsync();
        var second = await Cards.Nth(1).BoundingBoxAsync();
        Assert.That(grid, Is.Not.Null);

        // From the gap below the first row's cards, up across the first two cards.
        var startX = first!.X + 10;
        var startY = Math.Max(first.Y + first.Height, second!.Y + second.Height) + 6;
        await Page.Mouse.MoveAsync(startX, startY);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(startX + 20, startY - 20, new() { Steps = 3 });
        await Page.Mouse.MoveAsync(second.X + 10, second.Y + second.Height / 2, new() { Steps = 5 });
        await Expect(Page.GetByTestId("doc-marquee")).ToBeVisibleAsync();
        await Page.Mouse.UpAsync();

        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("2 selected");
        await Expect(Cards.Nth(0)).ToHaveAttributeAsync("data-selected", "true");
        await Expect(Cards.Nth(1)).ToHaveAttributeAsync("data-selected", "true");
        await Expect(Page.GetByTestId("doc-marquee")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("doc-viewer")).ToHaveCountAsync(0);

        // A plain click on empty space clears the selection.
        await Page.Mouse.ClickAsync(startX, startY);
        await Expect(Page.GetByTestId("docs-selbar")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Several_selected_documents_download_as_one_zip_without_marking_them_read()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents?account={EditingSeed.BankAccountId}");
        await Expect(Cards).ToHaveCountAsync(3);
        await Cards.First.HoverAsync();
        await Checks.First.ClickAsync();
        await Checks.Nth(1).ClickAsync();

        var download = await Page.RunAndWaitForDownloadAsync(() => Page.GetByTestId("docs-sel-download").ClickAsync());
        Assert.That(download.SuggestedFilename, Does.EndWith(".zip"));
        var path = await download.PathAsync();
        await using (var zip = await ZipFile.OpenReadAsync(path))
        {
            Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
            {
                $"2026-03-28 {EditingSeed.BankAccountName} Statement 2026-03.pdf",
                $"2026-02-28 {EditingSeed.BankAccountName} Statement 2026-02.pdf",
            }));
        }
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("2 selected");   // the page stays
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(3);
    }

    [Test]
    public async Task Selected_documents_are_marked_read_and_unread()
    {
        await SeedDocumentsAsync();
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(SidebarCount).ToHaveTextAsync("4 new");
        await Page.GetByTestId("docs-mark-all").ClickAsync();
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(0);

        await Cards.First.HoverAsync();
        await Checks.First.ClickAsync();
        await Checks.Nth(1).ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Page.GetByTestId("docs-sel-unread").ClickAsync();
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(2);
        await Expect(SidebarCount).ToHaveTextAsync("2 new");

        await Checks.First.ClickAsync();
        await Page.GetByTestId("docs-sel-read").ClickAsync();
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(1);

        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Page.GetByTestId("doc-unread")).ToHaveCountAsync(1);
        await Expect(Cards.First).ToHaveAttributeAsync("data-unread", "true");   // the second was marked read again
    }

    [Test]
    public async Task The_selection_bar_fits_a_phone()
    {
        await SeedDocumentsAsync();
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/documents");
        await Checks.First.ClickAsync();
        var bar = Page.GetByTestId("docs-selbar");
        await Expect(bar).ToBeVisibleAsync();
        await Expect(bar.GetByTestId("docs-sel-unread")).ToBeInViewportAsync();
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        Assert.That(overflow, Is.False);
    }

    [Test]
    public async Task Imported_documents_without_an_account_show_their_bank_and_are_assigned_to_an_account_in_bulk()
    {
        await SeedDocumentsAsync();
        var store = Factory.Services.GetRequiredService<BankStatementStore>();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
            for (var m = 1; m <= 2; m++)
                await store.StoreAsync(db, owner.Id, null, "import", null,
                    new RemoteStatement($"mm:{m}", null, new DateOnly(2021, m, 28), $"Imported 2021-{m:D2}", Pdf($"Imported statement 2021-{m:D2}")), CancellationToken.None, "Example Bank");
        }

        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Cards).ToHaveCountAsync(6);
        await Page.GetByTestId("docs-account").SelectOptionAsync("bank:Example Bank");
        await Expect(Cards).ToHaveCountAsync(2);
        await Expect(Cards.First).ToContainTextAsync("Example Bank");

        await Cards.First.HoverAsync();
        await Checks.First.ClickAsync();
        await Page.GetByTestId("docs-sel-all").ClickAsync();
        await Expect(Page.GetByTestId("docs-sel-count")).ToHaveTextAsync("2 selected");
        await Page.GetByTestId("docs-sel-assign").ClickAsync();
        await Expect(Page.GetByTestId("assign-confirm")).ToBeDisabledAsync();
        await Page.GetByTestId("assign-account").SelectOptionAsync(EditingSeed.BankAccountId.ToString(CultureInfo.InvariantCulture));
        await Page.GetByTestId("assign-confirm").ClickAsync();

        await Expect(Page.GetByTestId("docs-selbar")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("documents-empty")).ToBeVisibleAsync();
        await Page.GetByTestId("docs-account").SelectOptionAsync(EditingSeed.BankAccountId.ToString(CultureInfo.InvariantCulture));
        await Expect(Cards).ToHaveCountAsync(5);
        await using var check = await Db.CreateDbContextAsync();
        var imported = await check.BankStatements.Where(s => s.Identifier.StartsWith("mm:")).ToListAsync();
        Assert.That(imported.Select(s => s.AccountId), Is.All.EqualTo(EditingSeed.BankAccountId));
    }
}
