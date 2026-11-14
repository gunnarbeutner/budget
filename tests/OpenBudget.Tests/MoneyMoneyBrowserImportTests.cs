using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The browser's MoneyMoney import: reading the ZIP of the data folder, and the whole run from chunked upload through
/// decryption to imported accounts, bookings and statements, with the temporary files gone afterwards.
/// </summary>
public sealed class MoneyMoneyBrowserImportTests : IAsyncDisposable
{
    private const string Owner = TestDb.DefaultOwner;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-mm-browser-{Guid.NewGuid():N}");
    private readonly TestDb _t = new();
    private readonly Connectors.CapturingLoggerFactory _log = new();

    public async ValueTask DisposeAsync()
    {
        await _t.DisposeAsync();
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
    }

    private string WriteZip(string password)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "MoneyMoney.zip");
        File.WriteAllBytes(path, MoneyMoneyZipFixture.Build(password));
        return path;
    }

    [Fact]
    public void Extract_writes_the_database_and_statements_and_skips_metadata_and_escaping_entries()
    {
        var zip = WriteZip("secret");
        var target = Path.Combine(_dir, "out");

        var folder = MoneyMoneyArchive.Extract(zip, target);

        Assert.True(File.Exists(folder.DatabasePath));
        Assert.Equal(2, folder.StatementFiles);
        Assert.True(File.Exists(Path.Combine(folder.StatementsDirectory, MoneyMoneyZipFixture.Bank, MoneyMoneyZipFixture.StatementFile)));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.pdf")));
        Assert.False(File.Exists(Path.Combine(target, "escape.pdf")));
        Assert.Empty(Directory.EnumerateFiles(target, "._*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_zip_without_a_database_is_refused()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "other.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var s = archive.CreateEntry("Documents/notes.txt").Open();
            s.Write("hello"u8);
        }

        var ex = Assert.Throws<InvalidDataException>(() => MoneyMoneyArchive.Extract(path, Path.Combine(_dir, "out")));
        Assert.Equal(MoneyMoneyArchive.NoDatabase, ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "plain.zip");
        File.WriteAllText(path, "not a zip");

        var ex = Assert.Throws<InvalidDataException>(() => MoneyMoneyArchive.Extract(path, Path.Combine(_dir, "out")));
        Assert.Equal(MoneyMoneyArchive.NotAZip, ex.Message);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private Task<(MoneyMoneyBrowserImport Import, string Staging)> ServiceAsync()
    {
        var store = new BankStatementStore(new BankStorageOptions(Path.Combine(_dir, "statements")));
        var importer = new MoneyMoneyImportService(_t.System, _t.Notifier, NullLogger<MoneyMoneyImportService>.Instance);
        var statements = new MoneyMoneyStatementImport(_t.System, store, importer, _t.Notifier);
        var staging = Path.Combine(_dir, "staging");
        return Task.FromResult((new MoneyMoneyBrowserImport(new MoneyMoneyStaging(staging), importer, statements, _t.System, _t.Notifier,
            new Logger<MoneyMoneyBrowserImport>(_log), new Lifetime()), staging));
    }

    /// <summary>Uploads <paramref name="zip"/> in chunks of <paramref name="chunk"/> bytes, once resending a chunk at a stale offset.</summary>
    private static async Task<string> UploadAsync(MoneyMoneyBrowserImport import, byte[] zip, int chunk)
    {
        var id = import.BeginUpload(Owner, zip.Length);
        long offset = 0;
        var resent = false;
        while (offset < zip.Length)
        {
            var size = (int)Math.Min(chunk, zip.Length - offset);
            var received = await import.AppendAsync(Owner, id, offset, new MemoryStream(zip, (int)offset, size), CancellationToken.None);
            Assert.NotNull(received);
            if (!resent && received > 0)
            {
                // A retried chunk at an old offset writes nothing and tells where to go on.
                resent = true;
                Assert.Equal(received, await import.AppendAsync(Owner, id, 0, new MemoryStream(zip, 0, size), CancellationToken.None));
            }
            offset = received.Value;
        }
        Assert.Null(import.Received("someone-else", id));
        return id;
    }

    private static async Task<MoneyMoneyImportJob> WaitAsync(MoneyMoneyBrowserImport import)
    {
        for (var i = 0; i < 300; i++)
        {
            if (import.GetJob(Owner) is { Running: false } job) return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("The import did not finish.");
    }

    [Fact]
    public async Task Upload_in_chunks_then_import_accounts_bookings_and_statements_and_clean_up()
    {
        var (import, staging) = await ServiceAsync();
        var zip = MoneyMoneyZipFixture.Build("secret");
        var id = await UploadAsync(import, zip, 1000);

        Assert.True(import.StartImport(Owner, id, "secret"));
        var job = await WaitAsync(import);

        Assert.True(job.Phase == MoneyMoneyImportPhase.Done, _log.All);
        Assert.DoesNotContain("secret", _log.All, StringComparison.Ordinal);
        Assert.Equal(2, job.Result!.AccountsImported);
        Assert.Equal(3, job.Result.TransactionsImported);
        Assert.Equal(1, job.Statements!.Stored);
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            Assert.Equal(new[] { "Example Checking", "Example Savings" }, await db.Accounts.Where(a => a.OwnerUserId == Owner).OrderBy(a => a.Name).Select(a => a.Name).ToListAsync());
            var statement = await db.BankStatements.SingleAsync();
            Assert.Equal("Statement January", statement.Name);
            Assert.Equal(RowIds.Namespace(1, 1), statement.AccountId);
            var looks = await db.Accounts.Where(a => a.OwnerUserId == Owner).OrderBy(a => a.Name).Select(a => a.Icon + " " + a.Color).ToListAsync();
            Assert.Equal(new[] { "bi-cash-coin #1d4ed8", " #2a7330" }, looks);
            var groups = await db.AccountGroups.Where(g => g.OwnerUserId == Owner).OrderBy(g => g.Name).Select(g => g.Icon + " " + g.Color).ToListAsync();
            Assert.Equal(new[] { "bi-bank #4b5563", " " }, groups);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task A_finished_import_hands_the_structure_to_OpenBudget()
    {
        var (import, _) = await ServiceAsync();
        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        Assert.True((await WaitAsync(import)).Phase == MoneyMoneyImportPhase.Done, _log.All);

        string ownerId;
        long accountId;
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            var source = await db.UserDataStates.SingleAsync(s => s.UserId == Owner);
            Assert.NotNull(source.StructureTakenOverAt);
            ownerId = source.UserId;
            accountId = await db.Accounts.Where(a => a.OwnerUserId == ownerId && a.Name == "Example Checking").Select(a => a.Id).SingleAsync();
        }
        var structure = new StructureService(_t.System, _t.System, _t.Notifier, new ChangeOrigin());
        Assert.Null(await structure.GetBlockAsync(ownerId));
        await structure.RenameAsync(StructureItemKind.Account, accountId, "Everyday account");
        await using (var db = await _t.System.CreateDbContextAsync())
            Assert.Equal("Everyday account", await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Name).SingleAsync());
    }

    [Fact]
    public async Task Restrict_to_of_categories_maps_to_the_imported_account_or_group()
    {
        var (import, _) = await ServiceAsync();
        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        var job = await WaitAsync(import);
        Assert.True(job.Phase == MoneyMoneyImportPhase.Done, _log.All);
        Assert.Equal(2, job.Result!.CategoriesRestricted);
        Assert.Equal(1, job.Result.CategoryRestrictionsUnknown);

        await using var db = await _t.System.CreateDbContextAsync();
        var byName = await db.Categories.Where(c => c.OwnerUserId == Owner && c.Name != "").ToDictionaryAsync(c => c.Name!);
        Assert.Equal(RowIds.Namespace(1, 2), byName["Cinema"].RestrictAccountId);
        Assert.Null(byName["Cinema"].RestrictAccountGroupId);
        Assert.Equal(RowIds.Namespace(1, 1), byName["Holiday"].RestrictAccountGroupId);
        Assert.Null(byName["Holiday"].RestrictAccountId);
        Assert.Null(byName["Salary"].RestrictAccountId);
        Assert.Null(byName["Salary"].RestrictAccountGroupId);
        Assert.Null(byName["Groceries"].RestrictAccountId);
    }

    [Theory]
    [InlineData("A12", 'A', 12)]
    [InlineData("G3", 'G', 3)]
    [InlineData("X3", '?', 0)]
    [InlineData("A", '?', 0)]
    [InlineData("Afoo", '?', 0)]
    public void Restriction_values_are_parsed(string value, char kind, long row) =>
        Assert.Equal((kind, row), MoneyMoneyImportService.ParseRestriction(value));

    [Fact]
    public async Task A_finished_import_shows_every_account_and_top_level_category_group_in_the_default_project()
    {
        var (import, _) = await ServiceAsync();
        var projectId = await UserDefaultProject.EnsureAsync(_t.System, Owner);
        Assert.NotNull(projectId);

        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        Assert.True((await WaitAsync(import)).Phase == MoneyMoneyImportPhase.Done, _log.All);

        await using var db = await _t.System.CreateDbContextAsync();
        var ownerId = Owner;
        var accounts = await db.Accounts.Where(a => a.OwnerUserId == ownerId).Select(a => a.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(accounts, await db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId).OrderBy(x => x).ToListAsync());
        var groups = await db.CategoryGroups.Where(g => g.OwnerUserId == ownerId && g.Name != "").OrderBy(g => g.Name).Select(g => g.Id).ToListAsync();
        Assert.Equal(2, groups.Count);
        Assert.Equal(groups.Order(), await db.ProjectCategoryAssignments.Where(a => a.ProjectId == projectId).Select(a => a.CategoryId).OrderBy(x => x).ToListAsync());
    }

    [Fact]
    public async Task A_first_import_that_fails_late_leaves_nothing_behind_and_can_be_retried()
    {
        var (import, _) = await ServiceAsync();
        var projectId = await UserDefaultProject.EnsureAsync(_t.System, Owner);
        import.AfterStatementsForTests = () => throw new IOException("Simulated storage failure");

        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        var failed = await WaitAsync(import);
        Assert.Equal((MoneyMoneyImportPhase.Failed, MoneyMoneyImportError.Failed), (failed.Phase, failed.Error));

        await using (var db = await _t.System.CreateDbContextAsync())
        {
            Assert.False(await db.Accounts.AnyAsync(a => a.OwnerUserId == Owner));
            Assert.False(await db.AccountGroups.AnyAsync(g => g.OwnerUserId == Owner));
            Assert.False(await db.Categories.AnyAsync(c => c.OwnerUserId == Owner));
            Assert.False(await db.CategoryGroups.AnyAsync(g => g.OwnerUserId == Owner));
            Assert.False(await db.Transactions.AnyAsync(t => t.OwnerUserId == Owner));
            Assert.False(await db.BankStatements.AnyAsync());
            Assert.False(await db.Budgets.AnyAsync(b => b.ProjectId == projectId));
            Assert.False(await db.ProjectAccountAssignments.AnyAsync(a => a.ProjectId == projectId));
            Assert.False(await db.ProjectCategoryAssignments.AnyAsync(a => a.ProjectId == projectId));
            Assert.False(await db.CategoryRules.AnyAsync(r => r.ProjectId == projectId));
            var state = await db.UserDataStates.SingleAsync(s => s.UserId == Owner);
            Assert.Equal((null, null), (state.LastUploadAt, state.StructureTakenOverAt));
        }
        var statements = Path.Combine(_dir, "statements");
        Assert.True(!Directory.Exists(statements) || !Directory.EnumerateFiles(statements, "*", SearchOption.AllDirectories).Any());

        import.AfterStatementsForTests = null;
        id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        var done = await WaitAsync(import);
        Assert.True(done.Phase == MoneyMoneyImportPhase.Done, _log.All);
        Assert.Equal(1, done.Statements!.Stored);
        await using var check = await _t.System.CreateDbContextAsync();
        Assert.Equal(2, await check.Accounts.CountAsync(a => a.OwnerUserId == Owner));
        Assert.Single(await check.Budgets.Where(b => b.ProjectId == projectId).ToListAsync());
    }

    [Fact]
    public async Task Category_budgets_become_the_active_MoneyMoney_budget()
    {
        var (import, _) = await ServiceAsync();
        var projectId = await UserDefaultProject.EnsureAsync(_t.System, Owner);

        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 4096);
        Assert.True(import.StartImport(Owner, id, "secret"));
        var job = await WaitAsync(import);
        Assert.True(job.Phase == MoneyMoneyImportPhase.Done, _log.All);
        Assert.Equal(1, job.Budgets!.SkippedTotal);

        await using var db = await _t.System.CreateDbContextAsync();
        var budget = await db.Budgets.Include(b => b.MonthlyEntries).SingleAsync(b => b.ProjectId == projectId);
        Assert.Equal((MoneyMoneyBudgetImport.BudgetName, DateTime.UtcNow.Year, true), (budget.Name, budget.Year, budget.IsActive));
        var names = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name);
        var byName = budget.MonthlyEntries.ToDictionary(e => names[e.CategoryId]!, StringComparer.Ordinal);
        Assert.Equal(["Cinema", "Groceries"], byName.Keys.Order(StringComparer.Ordinal));
        Assert.All(Enumerable.Range(1, 12), m => Assert.Equal(180m, byName["Groceries"].GetMonthAmount(m)));
        Assert.Equal(8.33m, byName["Cinema"].GetMonthAmount(1));
        Assert.Equal(100m, Enumerable.Range(1, 12).Sum(m => byName["Cinema"].GetMonthAmount(m)));
    }

    [Fact]
    public async Task Statements_with_non_ASCII_names_match_in_either_Unicode_form_and_are_cleaned_up()
    {
        var (import, staging) = await ServiceAsync();
        var zip = MoneyMoneyZipFixture.Build("secret", unicodeStatements: true);
        var id = await UploadAsync(import, zip, 4096);

        Assert.True(import.StartImport(Owner, id, "secret"));
        var job = await WaitAsync(import);

        Assert.True(job.Phase == MoneyMoneyImportPhase.Done, _log.All);
        Assert.Equal(3, job.Statements!.Found);
        Assert.Equal(3, job.Statements.Stored);
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            Assert.Equal(new[] { "Statement January", "Statement March", "Statement Savings" },
                await db.BankStatements.OrderBy(s => s.Name).Select(s => s.Name).ToListAsync());
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public void Leftovers_with_non_ASCII_names_are_deleted()
    {
        var staging = Path.Combine(_dir, "leftovers");
        var bank = Path.Combine(staging, "abc", "Statements", MoneyMoneyZipFixture.UnicodeBank.Normalize(System.Text.NormalizationForm.FormD));
        Directory.CreateDirectory(bank);
        File.WriteAllBytes(Path.Combine(bank, MoneyMoneyZipFixture.UnicodeFile), MoneyMoneyZipFixture.Pdf("left"));
        File.WriteAllBytes(Path.Combine(staging, "abc.zip"), [1]);

        MoneyMoneyBrowserImport.CleanLeftovers(staging);

        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task A_wrong_password_fails_clearly_and_leaves_nothing_behind()
    {
        var (import, staging) = await ServiceAsync();
        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 1 << 20);

        Assert.True(import.StartImport(Owner, id, "wrong"));
        var job = await WaitAsync(import);

        Assert.Equal(MoneyMoneyImportPhase.Failed, job.Phase);
        Assert.Equal(MoneyMoneyImportError.WrongPassword, job.Error);
        Assert.DoesNotContain("wrong", _log.All, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        await using var db = await _t.System.CreateDbContextAsync();
        Assert.Equal(0, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task A_second_MoneyMoney_import_is_refused()
    {
        var (import, staging) = await ServiceAsync();
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            (await RowIds.StateAsync(db, Owner)).LastUploadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var id = await UploadAsync(import, MoneyMoneyZipFixture.Build("secret"), 1 << 20);

        Assert.True(import.StartImport(Owner, id, "secret"));
        var job = await WaitAsync(import);

        Assert.Equal(MoneyMoneyImportError.AlreadyImported, job.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task An_incomplete_upload_does_not_start()
    {
        var (import, _) = await ServiceAsync();
        var zip = MoneyMoneyZipFixture.Build("secret");
        var id = import.BeginUpload(Owner, zip.Length);
        await import.AppendAsync(Owner, id, 0, new MemoryStream(zip, 0, 10), CancellationToken.None);

        Assert.False(import.StartImport(Owner, id, "secret"));
    }

    [Fact]
    public void Leftovers_of_an_earlier_run_are_removed()
    {
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "abc"));
        File.WriteAllText(Path.Combine(staging, "abc.zip"), "x");
        File.WriteAllText(Path.Combine(staging, "abc", "plain.sqlite"), "x");

        MoneyMoneyBrowserImport.CleanLeftovers(staging);

        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }
}
