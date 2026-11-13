using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// One copy per owner, content and date: documents arriving twice (bank login and MoneyMoney, or one login under two
/// identifiers) are folded into one row, and the cleanup command folds rows stored before. Synthetic PDFs only.
/// </summary>
public sealed class DocumentDedupeTests : IAsyncDisposable
{
    private const string Owner = "owner-1";
    private const string Other = "owner-2";
    private const int Login = 7;
    private const long Giro = 2_000_001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-dedupe-{Guid.NewGuid():N}");
    private readonly TestDb _t = new();
    private readonly BankStatementStore _store;

    public DocumentDedupeTests() => _store = new BankStatementStore(new BankStorageOptions(_dir));

    public async ValueTask DisposeAsync()
    {
        await _t.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static readonly DateOnly Day = new(2026, 3, 31);

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n%%EOF\n");

    private async Task<Account> GiroAsync()
    {
        await using var db = await _t.System.CreateDbContextAsync();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.Id == Giro) is { } existing) return existing;
        db.Users.Add(new AppUser { Id = Owner, UserName = "owner" });
        var account = new Account { Id = Giro, OwnerUserId = Owner, Name = "Giro" };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<BankStatement> StoreAsync(string owner, int? login, Account? account, string identifier, byte[] content, DateOnly? date = null)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await _store.StoreAsync(db, owner, login, login?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "import", account,
            new RemoteStatement(identifier, null, date ?? Day, "Statement", content), CancellationToken.None);
    }

    private async Task<List<BankStatement>> RowsAsync()
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await db.BankStatements.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    }

    private async Task<HashSet<string>> KnownAsync(int login)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await BankStatementStore.KnownIdentifiersAsync(db, login, CancellationToken.None);
    }

    [Fact]
    public async Task Bank_copy_after_an_imported_copy_takes_over_the_row()
    {
        var giro = await GiroAsync();
        var pdf = Pdf("one");
        await StoreAsync(Owner, null, giro, "mm:" + BankStatementStore.HashOf(pdf), pdf);

        await StoreAsync(Owner, Login, null, "postbox:1", pdf);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(Login, row.BankLoginId);
        Assert.Equal("postbox:1", row.Identifier);
        Assert.Equal(Giro, row.AccountId);
        Assert.Single(Directory.GetFiles(_dir, "*.pdf", SearchOption.AllDirectories));
        Assert.Contains("postbox:1", await KnownAsync(Login));
    }

    [Fact]
    public async Task Imported_copy_after_a_bank_copy_is_skipped_and_brings_its_account()
    {
        var giro = await GiroAsync();
        var pdf = Pdf("one");
        await StoreAsync(Owner, Login, null, "postbox:1", pdf);

        await StoreAsync(Owner, null, giro, "mm:" + BankStatementStore.HashOf(pdf), pdf);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(Login, row.BankLoginId);
        Assert.Equal("postbox:1", row.Identifier);
        Assert.Equal(Giro, row.AccountId);
    }

    [Fact]
    public async Task Same_file_under_a_second_identifier_becomes_an_alias_the_sync_knows()
    {
        var pdf = Pdf("one");
        var first = await StoreAsync(Owner, Login, null, "postbox:1", pdf);

        var second = await StoreAsync(Owner, Login, null, "postbox:2", pdf);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await RowsAsync());
        var known = await KnownAsync(Login);
        Assert.Contains("postbox:1", known);
        Assert.Contains("postbox:2", known);
        Assert.Empty(await KnownAsync(Login + 1));
    }

    [Fact]
    public async Task Same_content_on_another_date_or_for_another_owner_is_kept()
    {
        var pdf = Pdf("notice");
        await StoreAsync(Owner, Login, null, "postbox:1", pdf);
        await StoreAsync(Owner, Login, null, "postbox:2", pdf, Day.AddDays(40));
        await StoreAsync(Other, Login + 1, null, "postbox:1", pdf);

        Assert.Equal(3, (await RowsAsync()).Count);
    }

    /// <summary>Rows stored before ingest folded copies: two sets for the owner, one notice resent on another date, another owner's copy.</summary>
    private async Task<(long BankCopy, long ImportCopy, long First, long Second)> SeedDuplicatesAsync()
    {
        await GiroAsync();
        Directory.CreateDirectory(_dir);
        await using var db = await _t.System.CreateDbContextAsync();
        var date = DateTime.SpecifyKind(Day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        static BankStatement Row(string owner, int? login, long? account, string identifier, string sha, DateTime when, string file) => new()
        {
            OwnerUserId = owner,
            BankLoginId = login,
            AccountId = account,
            Identifier = identifier,
            Sha256 = sha,
            Date = when,
            Name = "Statement",
            FilePath = file,
            SizeBytes = 1,
        };
        var import = Row(Owner, null, Giro, "mm:AAA", "AAA", date, "o/mm/a.pdf");
        var bank = Row(Owner, Login, null, "postbox:1", "AAA", date, "o/7/a.pdf");
        var first = Row(Owner, Login, null, "postbox:2", "BBB", date, "o/7/b1.pdf");
        var second = Row(Owner, Login, null, "postbox:3", "BBB", date.AddHours(10), "o/7/b2.pdf");
        db.BankStatements.AddRange(import, bank, first, second,
            Row(Owner, Login, null, "postbox:4", "BBB", date.AddDays(30), "o/7/b3.pdf"),
            Row(Other, Login + 1, null, "postbox:1", "AAA", date, "p/8/a.pdf"));
        await db.SaveChangesAsync();
        db.StatementReads.Add(new StatementRead { UserId = Owner, StatementId = import.Id });
        db.StatementReads.Add(new StatementRead { UserId = Owner, StatementId = second.Id, Unread = true });
        db.StatementReads.Add(new StatementRead { UserId = Owner, StatementId = first.Id });
        await db.SaveChangesAsync();
        foreach (var f in new[] { "o/mm/a.pdf", "o/7/a.pdf" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store.Resolve(f))!);
            await File.WriteAllBytesAsync(_store.Resolve(f), Pdf(f));
        }
        return (bank.Id, import.Id, first.Id, second.Id);
    }

    private async Task<DocumentDedupeResult> CleanupAsync(bool apply)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await DocumentDedupe.RunAsync(db, _store, apply, new List<string>());
    }

    [Fact]
    public async Task Dry_run_reports_and_changes_nothing()
    {
        await SeedDuplicatesAsync();
        var before = await RowsAsync();

        var result = await CleanupAsync(apply: false);

        Assert.Equal(new DocumentDedupeResult(2, 2, 1, 1, 0), result);
        Assert.Equal(before.Select(r => (r.Id, r.AccountId)), (await RowsAsync()).Select(r => (r.Id, r.AccountId)));
        await using var db = await _t.System.CreateDbContextAsync();
        Assert.Empty(await db.BankStatementAliases.ToListAsync());
        Assert.True(File.Exists(_store.Resolve("o/mm/a.pdf")));
    }

    [Fact]
    public async Task Apply_keeps_the_bank_copy_carries_account_and_reads_and_is_idempotent()
    {
        var (bank, import, first, second) = await SeedDuplicatesAsync();

        var result = await CleanupAsync(apply: true);

        Assert.Equal(new DocumentDedupeResult(2, 2, 1, 1, 1), result);
        var rows = await RowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == import || r.Id == second);
        var kept = rows.Single(r => r.Id == bank);
        Assert.Equal(Giro, kept.AccountId);
        Assert.False(File.Exists(_store.Resolve("o/mm/a.pdf")));
        Assert.True(File.Exists(_store.Resolve("o/7/a.pdf")));
        Assert.Contains("postbox:3", await KnownAsync(Login));
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            var reads = await db.StatementReads.AsNoTracking().ToListAsync();
            Assert.Contains(reads, r => r.StatementId == bank && r.UserId == Owner && !r.Unread);
            // the survivor's own read state wins over the deleted copy's "unread"
            Assert.False(Assert.Single(reads, r => r.StatementId == first).Unread);
        }

        Assert.Equal(new DocumentDedupeResult(0, 0, 0, 0, 0), await CleanupAsync(apply: true));
        Assert.Equal(4, (await RowsAsync()).Count);
    }
}
