using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the /sources page: it lists the registered sources and only lets you delete one
/// that no project depends on. The baseline world seeds a referenced "legacy" source (delete must
/// be refused) and an unreferenced "spare" source (delete allowed).
/// </summary>
[TestFixture]
public sealed class SourcesTests : EndToEndTestBase
{
    private ILocator Row(string sourceName) =>
        Page.GetByTestId("source-row").Filter(new() { HasText = sourceName });

    [Test]
    public async Task Sources_page_lists_both_sources()
    {
        await GoToAsync("/sources");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Sources" }))
            .ToBeVisibleAsync();
        await Expect(Row("legacy")).ToBeVisibleAsync();
        await Expect(Row(E2ESeed.SpareSourceName)).ToBeVisibleAsync();
    }

    [Test]
    public async Task Referenced_source_cannot_be_deleted()
    {
        await GoToAsync("/sources");

        // The legacy/referenced source offers no delete button, just an "in use" marker.
        await Expect(Row("legacy").GetByText("in use")).ToBeVisibleAsync();
        await Expect(Row("legacy").GetByRole(AriaRole.Button, new() { Name = "Delete" }))
            .ToHaveCountAsync(0);

        // The spare/unreferenced source does offer one.
        await Expect(Row(E2ESeed.SpareSourceName).GetByRole(AriaRole.Button, new() { Name = "Delete" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Deleting_an_unused_source_removes_it()
    {
        await GoToAsync("/sources");

        await Row(E2ESeed.SpareSourceName).GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText($"Delete source \"{E2ESeed.SpareSourceName}\"?")).ToBeVisibleAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        await Expect(Row(E2ESeed.SpareSourceName)).ToHaveCountAsync(0);
        // The referenced source is untouched.
        await Expect(Row("legacy")).ToBeVisibleAsync();
    }
}
