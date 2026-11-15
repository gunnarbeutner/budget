using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The view headers stay readable with a wide sidebar: no header control overlaps another, and the header never
/// overlaps the row below it (the summary actions under the Transactions header used to be covered by a wrapped
/// Filter button).
/// </summary>
[TestFixture]
public sealed class HeaderLayoutTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedAccounts() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    [TestCase(1280)]
    [TestCase(1600)]
    public async Task Header_controls_do_not_overlap_with_a_wide_sidebar(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.AddInitScriptAsync("try { localStorage.setItem('ob.sidebarWidth', '520'); } catch (e) {}");
        var pid = E2ESeed.ProjectId;

        await GoToAsync($"/projects/{pid}/browse?year=2026&month=3&sel=acct-{EditingSeed.ImportedAccountId}");
        var toolbar = Page.GetByTestId("browse-toolbar");
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();
        var rail = await Page.Locator(".browse-rail").BoundingBoxAsync();
        Assert.That(rail!.Width, Is.EqualTo(520).Within(2), "the sidebar keeps its resized width");
        await AssertNoOverlapAsync(toolbar,
            ".browse-ident, .browse-balance, .period-stepper, .segmented, .browse-search, [data-testid=filter-toggle]");
        await Expect(toolbar.GetByTestId("browse-summary")).ToBeVisibleAsync();

        foreach (var (path, testId) in new[]
                 {
                     ($"/projects/{pid}/budgets", "budget-toolbar"),
                     ($"/projects/{pid}/portfolio", "portfolio-toolbar"),
                     ($"/projects/{pid}/documents", "documents-toolbar"),
                 })
        {
            await GoToAsync(path);
            var bar = Page.GetByTestId(testId);
            await Expect(bar).ToBeVisibleAsync();
            await AssertNoOverlapAsync(bar, ":scope > *:not(.flex-grow-1)");
        }
    }

    /// <summary>
    /// On a phone the top bar's controls share one centre line and the rows under it (project switcher, period
    /// stepper, title) start at the same left edge, the summary actions follow the title on its centre line, and the
    /// period select and the total end at the avatar's right edge. At desktop width it only takes screenshots. Set OB_PHONE_HEADER_SCREENSHOT_DIR to keep screenshots for review.
    /// </summary>
    [TestCase("light", 390)]
    [TestCase("dark", 390)]
    [TestCase("light", 1280)]
    [TestCase("dark", 1280)]
    public async Task Phone_header_lines_up(string theme, int width)
    {
        var dir = Environment.GetEnvironmentVariable("OB_PHONE_HEADER_SCREENSHOT_DIR");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await using (var db = await Factory.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync())
        {
            db.BankLogins.Add(new BankLogin
            {
                OwnerUserId = E2ESeed.OwnerUserId,
                DisplayName = "Needs you",
                ConnectorType = "fake",
                LastSyncAt = DateTime.UtcNow.AddHours(-1),
                NeedsManualSync = "The bank wants a TAN or code.",
                NeedsManualSince = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await Page.SetViewportSizeAsync(width, 844);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year={E2ESeed.BudgetYear}&month=1");
        await Expect(Page.GetByTestId("bank-sync-attention")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("browse-balance")).ToBeVisibleAsync();
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"header-{width}-{theme}.png") });
        if (width > 400) return;

        async Task<(float Left, float Right, float CentreY, float Height)> Box(string selector)
        {
            var b = (await Page.Locator(selector).First.BoundingBoxAsync())!;
            return (b.X, b.X + b.Width, b.Y + b.Height / 2, b.Height);
        }
        var pill = await Box(".app-topbar .project-selector-toggle");
        var icons = new[]
        {
            await Box(".app-topbar .topbar-search i"), await Box(".app-topbar > .app-sync i"),
            await Box(".app-topbar .app-sync-attention"), await Box(".app-topbar .app-avatar"),
        };
        foreach (var icon in icons)
            Assert.That(icon.CentreY, Is.EqualTo(pill.CentreY).Within(1), "top bar icons on the pill's centre line");
        for (var i = 1; i < icons.Length; i++)
            Assert.That((icons[i].Left + icons[i].Right) / 2 - (icons[i - 1].Left + icons[i - 1].Right) / 2, Is.EqualTo(44).Within(1),
                "top bar icons evenly spaced");

        // The bootstrap-icons chevron draws its stroke from 4.65/16 of its box.
        var chevron = await Box(".app-topbar .period-step i");
        Assert.That((await Box(".app-topbar .project-selector-name")).Left, Is.EqualTo(16).Within(1), "project name on the gutter");
        Assert.That(chevron.Left + (chevron.Right - chevron.Left) * 4.65f / 16, Is.EqualTo(16).Within(1), "chevron stroke on the gutter");
        Assert.That((await Box(".browse-title-btn > span")).Left, Is.EqualTo(16).Within(1), "title on the gutter");
        var title = await Box(".browse-title-btn > span");
        var actions = Page.Locator(".browse-toolbar .browse-summary > .browse-action");
        Assert.That(await actions.CountAsync(), Is.GreaterThanOrEqualTo(3), "filter, summary and mark-all actions");
        var previousRight = title.Right;
        for (var i = 0; i < await actions.CountAsync(); i++)
        {
            var a = (await actions.Nth(i).BoundingBoxAsync())!;
            Assert.That(a.Y + a.Height / 2, Is.EqualTo(title.CentreY).Within(1), "actions on the title's centre line");
            Assert.That(a.Height, Is.EqualTo(44).Within(1), "44px touch target");
            Assert.That(a.X, Is.GreaterThanOrEqualTo(previousRight - 1), "actions follow the title in order");
            previousRight = a.X + a.Width;
        }

        var mode = await Box(".app-topbar .period-mode-select");
        Assert.That(mode.Height, Is.EqualTo(pill.Height).Within(1), "period select as tall as the project pill");
        Assert.That(mode.Right, Is.EqualTo(icons[^1].Right).Within(1), "period select ends at the avatar's edge");
        Assert.That((await Box("[data-testid=browse-balance]")).Right, Is.EqualTo(icons[^1].Right).Within(1), "total ends at the avatar's edge");

        // The summary toggle shows its state in the accent colour (open) or the muted colour (closed), never a fill.
        var toggle = Page.GetByTestId("summary-toggle");
        const string transparent = "rgba(0, 0, 0, 0)";
        async Task<string> Colour(string variable) => await Page.EvaluateAsync<string>(@"v => {
            const e = document.createElement('span'); e.style.color = `var(${v})`; document.body.append(e);
            const c = getComputedStyle(e).color; e.remove(); return c; }", variable);
        await Expect(Page.GetByTestId("summary-strip")).ToBeVisibleAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(toggle).ToHaveCSSAsync("background-color", transparent);
        await Expect(toggle).ToHaveCSSAsync("color", await Colour("--ob-accent"));
        await toggle.ClickAsync();
        await Expect(Page.GetByTestId("summary-strip")).ToHaveCountAsync(0);
        await Page.Mouse.MoveAsync(0, 400);
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(toggle).ToHaveCSSAsync("background-color", transparent);
        await Expect(toggle).ToHaveCSSAsync("color", await Colour("--ob-muted"));
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"header-{width}-{theme}-summary-closed.png") });
    }

    private async Task SeedNovemberTotalAsync(long amount)
    {
        var accountId = RowIds.Namespace(E2ESeed.IdSpace, 78);
        await using var db = await Factory.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        db.Accounts.Add(new Account
        {
            Id = accountId,
            OwnerUserId = E2ESeed.OwnerUserId,
            Name = "A very long account name for a narrow phone",
            Type = 1,
            Currency = "EUR",
        });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = accountId });
        var date = ((DateTimeOffset)new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        db.Transactions.Add(new Transaction
        {
            Id = 7900,
            OwnerUserId = E2ESeed.OwnerUserId,
            AccountId = accountId,
            Amount = amount,
            CategoryId = 200,
            Currency = "EUR",
            Name = "Big",
            Timestamp = date,
            ValueTimestamp = date,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// On a phone with a typical total the title row has room for everything: the title shows in full with its
    /// chevron, the icon actions sit packed right after it and the total ends at the avatar's edge. The summary card
    /// labels its spending without the period, which the header already names.
    /// </summary>
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Phone_title_row_fits_a_typical_total(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_PHONE_HEADER_SCREENSHOT_DIR");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await SeedNovemberTotalAsync(-2017593);
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11");
        var balance = Page.GetByTestId("browse-balance");
        await Expect(balance).ToContainTextAsync(",");
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();
        Assert.That((await balance.InnerTextAsync()).Count(char.IsDigit), Is.GreaterThanOrEqualTo(7), "a total with 5 digits before the comma");
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"title-row-390-{theme}.png") });

        var title = Page.Locator(".browse-title-btn > span");
        Assert.That(await title.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth"), Is.True, "the title is not cut");
        var chevron = Page.Locator(".browse-title-btn > i");
        await Expect(chevron).ToBeVisibleAsync();
        var chevronBox = (await chevron.BoundingBoxAsync())!;
        var heading = (await Page.Locator(".browse-title").BoundingBoxAsync())!;
        Assert.That(chevronBox.Width, Is.GreaterThan(8), "the chevron keeps its width");
        Assert.That(chevronBox.X + chevronBox.Width, Is.LessThanOrEqualTo(heading.X + heading.Width + 1), "the chevron is not clipped");

        var actions = Page.Locator(".browse-toolbar .browse-summary > .browse-action");
        Assert.That(await actions.CountAsync(), Is.GreaterThanOrEqualTo(3), "filter, summary and mark-all actions");
        var previousRight = chevronBox.X + chevronBox.Width;
        for (var i = 0; i < await actions.CountAsync(); i++)
        {
            var a = (await actions.Nth(i).BoundingBoxAsync())!;
            Assert.That(a.Width, Is.EqualTo(40).Within(0.5), "40px action");
            Assert.That(a.X - previousRight, Is.InRange(-1, i == 0 ? 12 : 4), "actions packed after the title");
            previousRight = a.X + a.Width;
        }
        var total = (await balance.BoundingBoxAsync())!;
        var avatar = (await Page.Locator(".app-topbar .app-avatar").BoundingBoxAsync())!;
        Assert.That(previousRight, Is.LessThanOrEqualTo(total.X), "actions before the total");
        Assert.That(total.X + total.Width, Is.EqualTo(avatar.X + avatar.Width).Within(1), "total ends at the avatar's edge");

        var label = Page.GetByTestId("tile-spent").Locator(".summary-label");
        Assert.That(await label.InnerTextAsync(), Is.EqualTo("Spent of budget"));
        Assert.That(await label.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"), Is.True, "the card label is not cut");
    }

    /// <summary>
    /// On a phone a very large total keeps its width and the title row stays one line: the title gives way with an
    /// ellipsis while its chevron stays, the actions keep their boxes after it and the total still ends at the avatar's edge.
    /// </summary>
    [Test]
    public async Task Phone_title_truncates_for_a_large_total()
    {
        await SeedNovemberTotalAsync(-98765432198);
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11");
        var balance = Page.GetByTestId("browse-balance");
        await Expect(balance).ToContainTextAsync("987");
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();
        // Squeeze the row further with a long title so it has to give way.
        await Page.Locator(".browse-title-btn > span").EvaluateAsync("e => e.textContent = 'All transactions of a long project name'");

        var titleBox = (await Page.Locator(".browse-title").BoundingBoxAsync())!;
        var avatar = (await Page.Locator(".app-topbar .app-avatar").BoundingBoxAsync())!;
        var total = (await balance.BoundingBoxAsync())!;
        Assert.That(await Page.Locator(".browse-title-btn > span").EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"),
            Is.True, "the title is cut");
        var chevron = (await Page.Locator(".browse-title-btn > i").BoundingBoxAsync())!;
        Assert.That(chevron.Width, Is.GreaterThan(8), "the chevron stays");
        Assert.That(chevron.X + chevron.Width, Is.LessThanOrEqualTo(titleBox.X + titleBox.Width + 1), "the chevron is not clipped");
        Assert.That(await balance.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"), Is.True, "the total is not cut");
        Assert.That(total.Height, Is.LessThan(30), "the total stays on one line");
        Assert.That(titleBox.Height, Is.LessThan(30), "the title stays on one line");
        Assert.That(total.X + total.Width, Is.EqualTo(avatar.X + avatar.Width).Within(1), "total ends at the avatar's edge");
        var actions = Page.Locator(".browse-toolbar .browse-summary > .browse-action");
        Assert.That(await actions.CountAsync(), Is.GreaterThanOrEqualTo(2), "the actions show");
        for (var i = 0; i < await actions.CountAsync(); i++)
        {
            var a = (await actions.Nth(i).BoundingBoxAsync())!;
            Assert.That(a.Y + a.Height / 2, Is.EqualTo(total.Y + total.Height / 2).Within(2), "actions on the total's line");
            Assert.That(a.Width, Is.GreaterThanOrEqualTo(39.5), "actions keep their width");
            Assert.That(a.X, Is.GreaterThanOrEqualTo(titleBox.X + titleBox.Width - 1), "actions after the title");
            Assert.That(a.X + a.Width, Is.LessThanOrEqualTo(total.X + 1), "actions before the total");
        }
    }

    /// <summary>
    /// Transactions, Budget and Cashflow show the period selector in the top bar, after the project switcher and
    /// before the sync status, gear and account menu; below 1200px it takes a second row under the bar. The views'
    /// own toolbars carry none, and the page never scrolls sideways.
    /// </summary>
    [TestCase(1280)]
    [TestCase(390)]
    public async Task Period_selector_sits_in_the_top_bar(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        var pid = E2ESeed.ProjectId;
        foreach (var (path, toolbarSelector) in new[]
                 {
                     ($"/projects/{pid}/browse?year=2026&month=3", "[data-testid=browse-toolbar]"),
                     ($"/projects/{pid}/budgets", "[data-testid=budget-toolbar]"),
                     ($"/projects/{pid}/cashflow", ".page-toolbar"),
                 })
        {
            await GoToAsync(path);
            var topbar = Page.GetByTestId("app-topbar");
            var nav = topbar.GetByTestId("period-nav");
            await Expect(nav).ToBeVisibleAsync();
            await Expect(Page.Locator(toolbarSelector).First.GetByTestId("period-nav")).ToHaveCountAsync(0);
            var switcher = (await topbar.GetByTestId("project-selector").BoundingBoxAsync())!;
            var user = (await topbar.GetByTestId("user-menu").BoundingBoxAsync())!;
            var bar = (await topbar.BoundingBoxAsync())!;
            var box = (await nav.BoundingBoxAsync())!;
            if (width >= 1200)
            {
                Assert.That(box.X, Is.GreaterThanOrEqualTo(switcher.X + switcher.Width - 1), $"{path}: right of the project switcher");
                Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(user.X + 1), $"{path}: left of the account menu");
                Assert.That(box.Y + box.Height / 2, Is.EqualTo(switcher.Y + switcher.Height / 2).Within(4), $"{path}: on the bar's row");
            }
            else
            {
                Assert.That(box.Y, Is.GreaterThanOrEqualTo(switcher.Y + switcher.Height - 1), $"{path}: on a row under the switcher");
                Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(bar.Y + bar.Height + 1), $"{path}: inside the bar");
            }
            // The page's sticky toolbar starts under the (taller) bar instead of under its first row.
            var toolbar = (await Page.Locator(toolbarSelector).First.BoundingBoxAsync())!;
            Assert.That(toolbar.Y, Is.GreaterThanOrEqualTo(bar.Y + bar.Height - 1), $"{path}: the toolbar sits below the bar");
            // Polls so a frame mid-render (before the bar wraps) is not taken for the final layout.
            try { await Page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth", null, new() { Timeout = 5000 }); }
            catch (TimeoutException) { /* the assertion below names the overflowing elements */ }
            Assert.That(await Page.EvaluateAsync<string>(@"() => document.documentElement.scrollWidth <= window.innerWidth ? '' :
                [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > window.innerWidth + 1)
                    .slice(0, 5).map(e => e.tagName + '.' + e.className).join(' | ')"), Is.Empty,
                $"{path}: no horizontal scroll");
        }

        await GoToAsync($"/projects/{pid}/portfolio");
        await Expect(Page.GetByTestId("app-topbar")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("period-nav")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// A busy account (IBAN info line, balance, a full month of rows) in a tall window: the browse column is a
    /// height-bound flex column, and the long ledger used to squeeze the two-row toolbar down to its 48px
    /// min-height so its second row (period, search, Filter) was drawn over the summary row.
    /// </summary>
    [TestCase(2000, 1100, 260)]
    [TestCase(2560, 1300, 640)]
    [TestCase(1280, 900, 180)]
    [TestCase(1024, 768, 640)]
    [TestCase(1600, 900, 520)]
    [TestCase(390, 844, 260)]
    public async Task Toolbar_and_summary_stack_for_a_busy_account(int width, int height, int sidebar)
    {
        var accountId = RowIds.Namespace(E2ESeed.IdSpace, 77);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Accounts.Add(new Account
            {
                Id = accountId,
                OwnerUserId = E2ESeed.OwnerUserId,
                Name = "Busy Giro",
                Iban = "DE89370400440532013000",
                Type = 1,
                Currency = "EUR",
                Balances = "11:-376.120000,3:EUR,",
            });
            await db.SaveChangesAsync();
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = accountId });
            for (var i = 0; i < 120; i++)
            {
                var date = new DateTime(2026, 11, 1 + i % 28, 0, 0, 0, DateTimeKind.Utc);
                db.Transactions.Add(new Transaction
                {
                    Id = 7000 + i,
                    OwnerUserId = E2ESeed.OwnerUserId,
                    AccountId = accountId,
                    Amount = -(100 + i),
                    CategoryId = 200,
                    Currency = "EUR",
                    Name = $"Payee {i}",
                    Timestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
                    ValueTimestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
                });
            }
            await db.SaveChangesAsync();
        }

        await Page.SetViewportSizeAsync(width, height);
        await Page.AddInitScriptAsync($"try {{ localStorage.setItem('ob.sidebarWidth', '{sidebar}'); }} catch (e) {{}}");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11&sel=acct-{accountId}");
        var toolbar = Page.GetByTestId("browse-toolbar");
        var summary = Page.GetByTestId("browse-summary");
        await Expect(summary).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();

        var tb = (await toolbar.BoundingBoxAsync())!;
        var scrollHeight = await toolbar.EvaluateAsync<double>("e => e.scrollHeight");
        Assert.That(scrollHeight, Is.LessThanOrEqualTo(tb.Height + 1), "the toolbar is as tall as its content");
        await AssertNoOverlapAsync(toolbar,
            ".browse-ident, .browse-balance, .period-stepper, .segmented, .browse-search, [data-testid=filter-toggle]");
        await Expect(toolbar.GetByTestId("browse-summary")).ToBeVisibleAsync();
        var docOverflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        Assert.That(docOverflow, Is.False, "no horizontal page scroll");

        // The same rows under a category with its rules bar open: toolbar, rules bar and summary stack, and the
        // whole "+ Rule" button takes clicks (it used to be partly covered when the squeezed rows overlapped).
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11&sel=cat-200&rules=1");
        var rulesBar = Page.GetByTestId("rules-bar");
        var add = Page.GetByTestId("rules-add");
        await Expect(add).ToBeVisibleAsync();
        await Expect(summary).ToBeVisibleAsync();
        await AssertBelowAsync(toolbar, rulesBar);
        var barBox = (await rulesBar.BoundingBoxAsync())!;
        Assert.That(await rulesBar.EvaluateAsync<double>("e => e.scrollHeight"), Is.LessThanOrEqualTo(barBox.Height + 1),
            "the rules bar is as tall as its content");
        var misses = await add.EvaluateAsync<string[]>(@"b => {
            const r = b.getBoundingClientRect(), out = [];
            for (let fx = 0.05; fx < 1; fx += 0.15) for (let fy = 0.1; fy < 1; fy += 0.2) {
                const hit = document.elementFromPoint(r.left + r.width * fx, r.top + r.height * fy);
                if (!hit || !b.contains(hit)) out.push(Math.round(fx * 100) + '%,' + Math.round(fy * 100) + '% -> ' + (hit ? hit.tagName + '.' + hit.className : 'none'));
            }
            return out;
        }");
        Assert.That(misses, Is.Empty, "every point of '+ Rule' hits the button");
    }

    /// <summary>Asserts the visible elements matching <paramref name="selector"/> inside the container are
    /// pairwise disjoint and lie within the container's box.</summary>
    private async Task AssertNoOverlapAsync(ILocator container, string selector)
    {
        var outer = (await container.BoundingBoxAsync())!;
        var boxes = new List<(string Name, LocatorBoundingBoxResult Box)>();
        var items = container.Locator(selector);
        for (var i = 0; i < await items.CountAsync(); i++)
        {
            var item = items.Nth(i);
            if (!await item.IsVisibleAsync()) continue;
            var box = await item.BoundingBoxAsync();
            if (box is null || box.Width < 1 || box.Height < 1) continue;
            boxes.Add((await item.EvaluateAsync<string>("e => e.className || e.tagName"), box));
        }
        Assert.That(boxes, Has.Count.GreaterThan(1), "found the header controls");

        foreach (var (name, b) in boxes)
        {
            Assert.That(b.Y, Is.GreaterThanOrEqualTo(outer.Y - 1), $"{name} sticks out above the header");
            Assert.That(b.Y + b.Height, Is.LessThanOrEqualTo(outer.Y + outer.Height + 1), $"{name} sticks out below the header");
            Assert.That(b.X + b.Width, Is.LessThanOrEqualTo(outer.X + outer.Width + 1), $"{name} sticks out to the right");
        }
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (an, a) = boxes[i];
                var (bn, b) = boxes[j];
                var overlap = a.X < b.X + b.Width - 1 && b.X < a.X + a.Width - 1 && a.Y < b.Y + b.Height - 1 && b.Y < a.Y + a.Height - 1;
                Assert.That(overlap, Is.False, $"'{an}' overlaps '{bn}'");
            }
    }

    private async Task AssertBelowAsync(ILocator header, ILocator next)
    {
        var h = (await header.BoundingBoxAsync())!;
        var n = (await next.BoundingBoxAsync())!;
        Assert.That(n.Y, Is.GreaterThanOrEqualTo(h.Y + h.Height - 1), "the row below starts under the header");
    }
}
