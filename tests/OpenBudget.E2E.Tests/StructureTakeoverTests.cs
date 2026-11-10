using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>The one-time "Take over structure from MoneyMoney" action on the Sources page.</summary>
[TestFixture]
public sealed class StructureTakeoverTests : EndToEndTestBase
{
    [SetUp]
    public async Task MarkSourceUploaded()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var source = await db.ImportSources.SingleAsync(s => s.Id == E2ESeed.SourceId);
        source.LastUploadAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Taking_over_the_structure_asks_for_confirmation_and_then_marks_the_source()
    {
        await GoToAsync("/sources");
        await Page.GetByTestId("structure-takeover").First.ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToContainTextAsync("cannot be undone");
        await Page.GetByTestId("structure-takeover-confirm").ClickAsync();

        await Expect(Page.GetByTestId("structure-owned")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("structure-takeover")).ToHaveCountAsync(0);

        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.ImportSources.SingleAsync(s => s.Id == E2ESeed.SourceId)).StructureTakenOverAt, Is.Not.Null);
    }
}
