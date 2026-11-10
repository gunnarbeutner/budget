using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Desktop shell: header + sidebar + main fill the viewport and only the inner panes scroll; the document itself
/// never does (otherwise the shell ends part-way down and blank page shows below), and the phone bottom bar stays hidden.
/// A short window makes every view's content longer than the viewport.
/// </summary>
[TestFixture]
public sealed class DocumentScrollTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    [TestCase("budgets")]
    [TestCase("browse")]
    [TestCase("cashflow")]
    [TestCase("portfolio")]
    [TestCase("documents")]
    public async Task The_document_does_not_scroll_on_desktop(string view)
    {
        await Page.SetViewportSizeAsync(2000, 300);
        await GoToAsync($"/projects/{Pid}/{view}");
        await Expect(Page.Locator(".browse-main")).ToBeVisibleAsync();

        // What reaches below the viewport without a scrolling pane around it, for the failure message.
        var culprits = await Page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('body *')]
            .filter(el => el.getBoundingClientRect().bottom > innerHeight + 1)
            .filter(el => { for (let p = el.parentElement; p && p !== document.body; p = p.parentElement)
                                if (getComputedStyle(p).overflowY !== 'visible') return false;
                            return true; })
            .slice(0, 20).map(el => el.tagName + '.' + el.className).join('\n')");
        var scrollHeight = await Page.EvaluateAsync<int>("document.scrollingElement.scrollHeight");
        var innerHeight = await Page.EvaluateAsync<int>("innerHeight");
        Assert.That(scrollHeight, Is.LessThanOrEqualTo(innerHeight), culprits);
        await Expect(Page.GetByTestId("bottom-bar")).ToBeHiddenAsync();
    }
}
