using System.IO.Compression;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The OneDrive section of Settings › Import against the fake Microsoft: connect (the browser's trip to the
/// Microsoft sign-in page is answered by the fake), pick a ".portfolio" file, see it imported, recover from revoked
/// consent with "Reconnect", and disconnect.
/// </summary>
[TestFixture]
public sealed class OneDriveSettingsTests : EndToEndTestBase
{
    private FakeMicrosoftCloud Microsoft => Factory.Microsoft;

    /// <summary>
    /// Stands in for the Microsoft sign-in page: the app's redirect to it is fetched from the app, the fake issues a
    /// code for its PKCE challenge, and the browser goes straight on to the callback.
    /// </summary>
    private async Task RouteMicrosoftSignInAsync() =>
        await Page.RouteAsync("**/connect/onedrive", async route =>
        {
            var response = await route.FetchAsync(new() { MaxRedirects = 0 });
            var location = response.Headers["location"];
            var (code, state, redirect) = Microsoft.Authorize(new Uri(location));
            await route.FulfillAsync(new()
            {
                Status = 302,
                Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Location"] = $"{redirect}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}",
                },
            });
        });

    private static byte[] PortfolioFile()
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity { Uuid = "sec-etf", Name = "Example World ETF", Isin = "XX0000000001" });
        var tax = new PTaxonomy { Id = "tax-regions", Name = "Regions" };
        tax.Classifications.Add(new PTaxonomy.Types.Classification { Id = "root", Name = "Regions" });
        var leaf = new PTaxonomy.Types.Classification { Id = "leaf", ParentId = "root", Name = "Europe" };
        leaf.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "sec-etf", Weight = 10000 });
        tax.Classifications.Add(leaf);
        client.Taxonomies.Add(tax);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("data.portfolio").Open();
            entry.Write("PPPBV1"u8);
            entry.Write(client.ToByteArray());
        }
        return zip.ToArray();
    }

    [Test]
    public async Task Connect_pick_a_file_import_reconnect_and_disconnect()
    {
        Microsoft.AccountName = "e2e@example.org";
        Microsoft.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "fin", ParentId = "root", Name = "Finance", IsFolder = true });
        Microsoft.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "notes", ParentId = "root", Name = "Notes.docx" });
        Microsoft.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "pp", ParentId = "fin", Name = "Depot.portfolio", Content = PortfolioFile() });
        await RouteMicrosoftSignInAsync();

        await GoToAsync("/portfolio-performance");
        var section = Page.GetByTestId("onedrive-section");
        await Expect(section).ToBeVisibleAsync();

        // Connect.
        await section.GetByTestId("onedrive-connect").ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("onedrive=connected", StringComparison.Ordinal));
        await WaitForInteractiveAsync();
        await Expect(section.GetByTestId("onedrive-notice")).ToContainTextAsync("Connected to Microsoft");
        await Expect(section.GetByTestId("onedrive-account")).ToContainTextAsync("e2e@example.org");
        await Expect(section.GetByTestId("onedrive-no-files")).ToBeVisibleAsync();

        // Pick the file: only folders and .portfolio files are offered.
        await section.GetByTestId("onedrive-pick").ClickAsync();
        var picker = Page.GetByTestId("onedrive-picker");
        await Expect(picker.Locator("[data-picker-item='Finance']")).ToBeVisibleAsync();
        await Expect(picker.Locator("[data-picker-item='Notes.docx']")).ToHaveCountAsync(0);
        await picker.Locator("[data-picker-item='Finance']").ClickAsync();
        await picker.Locator("[data-picker-item='Depot.portfolio']").ClickAsync();
        await Page.Locator("#onedrive-target").SelectOptionAsync(E2ESeed.SpareSourceName);
        await Page.GetByTestId("picker-link").ClickAsync();

        var row = section.Locator("[data-onedrive-file='My files/Finance/Depot.portfolio']");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.GetByTestId("onedrive-file-imported")).Not.ToHaveTextAsync("never");
        await Expect(row.GetByTestId("onedrive-file-error")).ToHaveCountAsync(0);
        var system = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await system.CreateDbContextAsync())
            Assert.That(await db.SecurityTaxonomies.CountAsync(t => t.SourceId == E2ESeed.SpareSourceId), Is.EqualTo(1));

        // Consent revoked at Microsoft: the next check asks for a reconnect.
        Microsoft.RejectRefresh = true;
        Microsoft.RevokeAccessTokens();
        await row.Locator("[data-action=check]").ClickAsync();
        await Expect(section.GetByTestId("onedrive-reconnect-needed")).ToBeVisibleAsync();

        Microsoft.RejectRefresh = false;
        await section.GetByTestId("onedrive-reconnect").ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("onedrive=connected", StringComparison.Ordinal));
        await WaitForInteractiveAsync();
        await Expect(section.GetByTestId("onedrive-reconnect-needed")).ToHaveCountAsync(0);
        await Expect(row).ToBeVisibleAsync();

        // Disconnect forgets the connection and the linked file.
        await section.GetByTestId("onedrive-disconnect").ClickAsync();
        await Page.GetByTestId("onedrive-disconnect-confirm").ClickAsync();
        await Expect(section.GetByTestId("onedrive-connect")).ToBeVisibleAsync();
        await Expect(row).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_sign_in_cancelled_at_Microsoft_is_reported()
    {
        await Page.RouteAsync("**/connect/onedrive", async route =>
        {
            var response = await route.FetchAsync(new() { MaxRedirects = 0 });
            var (_, state, redirect) = Microsoft.Authorize(new Uri(response.Headers["location"]));
            await route.FulfillAsync(new()
            {
                Status = 302,
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Location"] = $"{redirect}?error=access_denied&state={Uri.EscapeDataString(state)}" },
            });
        });

        await GoToAsync("/portfolio-performance");
        await Page.GetByTestId("onedrive-connect").ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("onedrive=denied", StringComparison.Ordinal));
        await WaitForInteractiveAsync();
        await Expect(Page.GetByTestId("onedrive-notice")).ToContainTextAsync("cancelled");
        await Expect(Page.GetByTestId("onedrive-connect")).ToBeVisibleAsync();
    }
}
