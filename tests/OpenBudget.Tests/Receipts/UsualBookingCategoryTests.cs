using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Receipt sources gaining their bookings' usual category, and the AI getting another try.</summary>
public sealed class UsualBookingCategoryTests
{
    private static async Task<TestDb> WorldAsync(int receipts)
    {
        var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AllowAsync(t, ReceiptWorld.Drugstore);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ProjectReceiptSources.Add(new ProjectReceiptSource { OwnerUserId = ReceiptWorld.Alice, ProjectId = ReceiptWorld.Project, Source = ReceiptSources.Mail, CategoryIds = ReceiptProjects.FormatIds([ReceiptWorld.Drugstore]) });
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < receipts; i++)
        {
            await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(500 + i), -1428 - i, days: i);
            await using var db = await t.System.CreateDbContextAsync();
            await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice($"PN1000000000000{i}"), $"mail-{i}", DateTime.UtcNow, default);
            var invoice = await db.ReceiptInvoices.OrderBy(r => r.Id).LastAsync();
            invoice.TransactionId = ReceiptWorld.Id(500 + i);
            invoice.State = ReceiptMatchState.Matched;
            foreach (var item in await db.ReceiptItems.Where(x => x.ReceiptInvoiceId == invoice.Id && x.Kind == ReceiptItemKind.Product).ToListAsync())
                item.CategorySource = ReceiptCategorySource.Ai;
            await db.SaveChangesAsync();
        }
        return t;
    }

    [Fact]
    public async Task A_source_gains_its_bookings_usual_category_and_the_ai_is_asked_again()
    {
        await using var t = await WorldAsync(3);
        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal(1, await ReceiptProjects.AllowUsualBookingCategoriesAsync(db, default));

        var rules = await ReceiptProjects.RulesAsync(db, ReceiptWorld.Alice, ReceiptWorld.Project, ReceiptSources.Mail, default);
        Assert.Equal([ReceiptWorld.Groceries, ReceiptWorld.Drugstore], rules.Allowed.Select(c => c.CategoryId).Order());
        Assert.False(await db.ReceiptItems.AnyAsync(i => i.CategorySource == ReceiptCategorySource.Ai));
        // Other sources keep what they had.
        var lidl = await ReceiptProjects.RulesAsync(db, ReceiptWorld.Alice, ReceiptWorld.Project, ReceiptSources.Of(ReceiptShops.Lidl), default);
        Assert.Equal([ReceiptWorld.Drugstore], lidl.Allowed.Select(c => c.CategoryId));

        // Once is enough.
        Assert.Equal(0, await ReceiptProjects.AllowUsualBookingCategoriesAsync(db, default));
    }

    [Fact]
    public async Task Too_few_bookings_change_nothing()
    {
        await using var t = await WorldAsync(2);
        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal(0, await ReceiptProjects.AllowUsualBookingCategoriesAsync(db, default));
        Assert.True(await db.ReceiptItems.AnyAsync(i => i.CategorySource == ReceiptCategorySource.Ai));
    }
}
