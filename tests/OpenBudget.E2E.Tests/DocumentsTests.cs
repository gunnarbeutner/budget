using System.Globalization;
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

    private static SystemDbContextFactory Db => AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>
    /// The editing world plus a fake-bank login of the owner linked to the giro account, with three statements of
    /// the giro (2026-01..03) and one document of the login without an account.
    /// </summary>
    internal static async Task SeedDocumentsAsync()
    {
        await EditingSeed.SeedAsync(Db);
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

        var store = AppFixture.Factory.Services.GetRequiredService<BankStatementStore>();
        var account = await db.Accounts.SingleAsync(a => a.Id == EditingSeed.BankAccountId);
        for (var m = 1; m <= 3; m++)
        {
            var date = new DateOnly(2026, m, 28);
            await store.StoreAsync(db, login, account, new RemoteStatement($"S-{m}", "FAKE-GIRO", date, $"Statement 2026-{m:D2}", Pdf($"Example statement 2026-{m:D2}")), CancellationToken.None);
        }
        await store.StoreAsync(db, login, null, new RemoteStatement("TAX-2025", null, new DateOnly(2026, 2, 1), "Tax certificate 2025", Pdf("Example tax certificate")), CancellationToken.None);
    }

    /// <summary>A minimal valid one-page PDF with a line of text (pdf.js renders it).</summary>
    private static byte[] Pdf(string text)
    {
        var stream = $"BT /F1 24 Tf 72 720 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
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
        await Expect(SidebarCount).ToHaveTextAsync("4");
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
        await Expect(SidebarCount).ToHaveTextAsync("4");

        await Cards.First.ClickAsync();
        var viewer = Page.GetByTestId("doc-viewer");
        await Expect(viewer).ToBeVisibleAsync();
        await Expect(viewer).ToContainTextAsync("28.03.2026");
        await Expect(viewer.Locator("canvas.doc-page")).ToHaveCountAsync(1, new() { Timeout = 15000 });
        await Expect(viewer.GetByTestId("doc-download")).ToHaveAttributeAsync("href", new Regex(@"/statements/\d+\?download=true$"));
        await Expect(SidebarCount).ToHaveTextAsync("3");

        // Arrow right moves to the next (older) document, which is then read too.
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(viewer).ToContainTextAsync("28.02.2026");
        await Expect(SidebarCount).ToHaveTextAsync("2");

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
        await Expect(SidebarCount).ToHaveTextAsync("3");
        await Page.GetByTestId("doc-close").ClickAsync();

        await Page.GetByTestId("docs-status").SelectOptionAsync("unread");
        await Expect(Page).ToHaveURLAsync(new Regex(@"status=unread"));
        await Expect(Cards).ToHaveCountAsync(3);

        await Page.GetByTestId("docs-account").SelectOptionAsync("none");
        await Expect(Cards).ToHaveCountAsync(1);
        await Expect(Cards.First).ToContainTextAsync("Example Bank");

        await Page.GetByTestId("docs-status").SelectOptionAsync("all");
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
        await Expect(SidebarCount).ToHaveTextAsync("1");   // the login's document outside the filter stays unread
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

        await using var db = await Db.CreateDbContextAsync();
        var link = await db.BankLoginAccounts.SingleAsync(a => a.LinkedAccountId == EditingSeed.BankAccountId);
        Assert.That(link.StatementsEnabled, Is.True);
        Assert.That(link.StatementsInterval, Is.EqualTo(StatementsInterval.Weekly));
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
}
