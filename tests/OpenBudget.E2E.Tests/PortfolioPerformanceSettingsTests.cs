using System.IO.Compression;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.PortfolioPerformance.Proto;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Portfolio Performance settings page: reached from the gear menu, a file uploaded in the browser lists its
/// portfolios with links to the portfolio accounts, depot, instrument and tax pages, labelled without the internal
/// source names.
/// </summary>
[TestFixture]
public sealed class PortfolioPerformanceSettingsTests : EndToEndTestBase
{
    internal static byte[] PortfolioFile(params string[] portfolios)
    {
        var client = new PClient();
        foreach (var name in portfolios)
            client.Portfolios.Add(new PPortfolio { Uuid = "pf-" + name, Name = name });
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
    public async Task The_settings_list_opens_the_page_and_an_uploaded_file_lists_its_portfolios()
    {
        await GoToAsync("/connections");
        await Page.GetByTestId("settings-nav-portfolio-performance").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/portfolio-performance$"));
        await Expect(Page.GetByTestId("settings-nav-portfolio-performance")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByTestId("pp-no-data")).ToBeVisibleAsync();

        await Page.Locator("#pp-upload-file").SetInputFilesAsync(new FilePayload
        {
            Name = "Example.portfolio",
            MimeType = "application/octet-stream",
            Buffer = PortfolioFile("Example Depot"),
        });
        await Expect(Page.GetByTestId("pp-upload-result")).ToContainTextAsync("Imported");
        var set = Page.GetByTestId("pp-data-set");
        await Expect(set).ToHaveCountAsync(1);
        // A single data set needs no label; internal source names never show.
        await Expect(set.GetByTestId("pp-data-label")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("pp-data")).Not.ToContainTextAsync("legacy");

        await set.GetByTestId("pp-portfolio-accounts").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/portfolio-performance/portfolios$"));
        await Expect(Page.GetByText("Example Depot", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("page-header")).Not.ToContainTextAsync("legacy");
    }

    [Test]
    public async Task A_file_that_is_not_a_portfolio_is_refused()
    {
        await GoToAsync("/portfolio-performance");
        await Page.Locator("#pp-upload-file").SetInputFilesAsync(new FilePayload
        {
            Name = "notes.portfolio",
            MimeType = "application/octet-stream",
            Buffer = "plain text"u8.ToArray(),
        });
        await Expect(Page.GetByTestId("pp-upload-result")).ToContainTextAsync("not a Portfolio Performance");
    }
}
