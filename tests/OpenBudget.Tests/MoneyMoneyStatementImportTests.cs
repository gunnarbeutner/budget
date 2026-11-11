using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Importing MoneyMoney's stored statements into the documents of the source's accounts, against a synthetic
/// MoneyMoney snapshot and synthetic PDFs.
/// </summary>
public sealed class MoneyMoneyStatementImportTests : IAsyncDisposable
{
    private const string SourceName = "mm";
    private const string Owner = "owner-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-mm-statements-{Guid.NewGuid():N}");
    private readonly TestDb _t = new();
    private readonly BankStatementStore _store;
    private readonly MoneyMoneyImportService _importer;
    private readonly MoneyMoneyStatementImport _statements;

    public MoneyMoneyStatementImportTests()
    {
        _store = new BankStatementStore(new BankStorageOptions(Path.Combine(_dir, "statements")));
        _importer = new MoneyMoneyImportService(_t.System, _t.Notifier, NullLogger<MoneyMoneyImportService>.Instance);
        _statements = new MoneyMoneyStatementImport(_t.System, _store, _importer, _t.Notifier);
    }

    public async ValueTask DisposeAsync()
    {
        await _t.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n%%EOF\n");

    /// <summary>Imports a snapshot with two accounts (MoneyMoney rowids 1 and 2) and gives the source an owner.</summary>
    private async Task<int> ImportSnapshotAsync()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "snapshot.sqlite");
        await using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
                CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
                CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
                CREATE TABLE statements (rowid INTEGER PRIMARY KEY, account_key INTEGER, name TEXT, directory TEXT, filename TEXT, created_tsp INTEGER, deleted INTEGER);
                INSERT INTO categories (name, type) VALUES ('', 1);
                INSERT INTO accounts (name, currency, type, balances) VALUES ('Giro', 'EUR', 1, '');
                INSERT INTO accounts (name, currency, type, balances) VALUES ('Depot', 'EUR', 1, '');
                INSERT INTO statements (account_key, name, directory, filename, created_tsp, deleted) VALUES (1, 'Statement 1', 'Bank', 'a.pdf', 1780000000, 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var result = await _importer.ImportFromFileAsync(SourceName, path, CancellationToken.None);
        await using var db = await _t.System.CreateDbContextAsync();
        var source = await db.ImportSources.SingleAsync(s => s.Id == result.SourceId);
        source.OwnerUserId = Owner;
        await db.SaveChangesAsync();
        return result.SourceId;
    }

    private async Task<List<BankStatement>> DocumentsAsync()
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await db.BankStatements.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    }

    [Fact]
    public async Task Statement_is_stored_under_the_namespaced_account_with_a_content_identifier()
    {
        var sourceId = await ImportSnapshotAsync();
        var pdf = Pdf("one");

        var outcome = await _statements.StoreAsync(SourceName, 2, new DateOnly(2026, 3, 31), "Monthly statement", pdf, CancellationToken.None);

        Assert.Equal(MoneyMoneyStatementOutcome.Stored, outcome);
        var doc = Assert.Single(await DocumentsAsync());
        var hash = BankStatementStore.HashOf(pdf);
        Assert.Equal(SourceScope.Namespace(sourceId, 2), doc.AccountId);
        Assert.Equal(sourceId, doc.SourceId);
        Assert.Null(doc.BankLoginId);
        Assert.Equal(Owner, doc.OwnerUserId);
        Assert.Equal("mm:" + hash, doc.Identifier);
        Assert.Equal(hash, doc.Sha256);
        Assert.Equal("Monthly statement", doc.Name);
        Assert.Equal(new DateTime(2026, 3, 31), doc.Date);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(_store.Resolve(doc.FilePath)));
    }

    [Fact]
    public async Task Repeated_uploads_store_each_document_once()
    {
        await ImportSnapshotAsync();
        var pdf = Pdf("repeat");
        var hash = BankStatementStore.HashOf(pdf);
        var refs = new[] { new MoneyMoneyStatementRef(1, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf))) };

        Assert.Equal([hash], await _statements.WantedAsync(SourceName, refs, CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.Stored, await _statements.StoreAsync(SourceName, 1, new DateOnly(2026, 1, 31), "A", pdf, CancellationToken.None));
        Assert.Empty(await _statements.WantedAsync(SourceName, refs, CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.Duplicate, await _statements.StoreAsync(SourceName, 1, new DateOnly(2026, 1, 31), "A", pdf, CancellationToken.None));
        Assert.Single(await DocumentsAsync());
    }

    [Fact]
    public async Task Document_a_bank_connector_already_downloaded_is_not_imported_again()
    {
        var sourceId = await ImportSnapshotAsync();
        var pdf = Pdf("from the bank");
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            var login = new BankLogin { OwnerUserId = Owner, ConnectorType = "test", DisplayName = "Bank" };
            db.BankLogins.Add(login);
            await db.SaveChangesAsync();
            var account = await db.Accounts.SingleAsync(a => a.Id == SourceScope.Namespace(sourceId, 1));
            await _store.StoreAsync(db, login, account, new OpenBudget.Connectors.RemoteStatement("bank-doc-1", "r1", new DateOnly(2026, 2, 28), "Statement", pdf), CancellationToken.None);
        }

        var refs = new[] { new MoneyMoneyStatementRef(1, BankStatementStore.HashOf(pdf)) };
        Assert.Empty(await _statements.WantedAsync(SourceName, refs, CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.Duplicate, await _statements.StoreAsync(SourceName, 1, new DateOnly(2026, 2, 28), "Statement", pdf, CancellationToken.None));
        var doc = Assert.Single(await DocumentsAsync());
        Assert.Equal("bank-doc-1", doc.Identifier);
    }

    [Fact]
    public async Task Documents_of_accounts_not_in_OpenBudget_are_kept_without_an_account_labelled_with_the_bank()
    {
        await ImportSnapshotAsync();
        var pdf = Pdf("orphan");
        var hash = BankStatementStore.HashOf(pdf);
        var refs = new[] { new MoneyMoneyStatementRef(99, hash) };

        Assert.Equal([hash], await _statements.WantedAsync(SourceName, refs, CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.Stored, await _statements.StoreAsync(SourceName, 99, new DateOnly(2021, 5, 31), "Statement", pdf, CancellationToken.None, "Example Bank"));

        var doc = Assert.Single(await DocumentsAsync());
        Assert.Null(doc.AccountId);
        Assert.Null(doc.SourceId);
        Assert.Null(doc.BankLoginId);
        Assert.Equal(Owner, doc.OwnerUserId);
        Assert.Equal("Example Bank", doc.Institution);
        Assert.Equal("mm:" + hash, doc.Identifier);

        // Uploading it again stores nothing new.
        Assert.Empty(await _statements.WantedAsync(SourceName, refs, CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.Duplicate, await _statements.StoreAsync(SourceName, 99, new DateOnly(2021, 5, 31), "Statement", pdf, CancellationToken.None, "Example Bank"));
        Assert.Single(await DocumentsAsync());
    }

    [Fact]
    public async Task Documents_of_a_known_account_carry_no_bank_label()
    {
        await ImportSnapshotAsync();
        await _statements.StoreAsync(SourceName, 1, new DateOnly(2026, 1, 31), "A", Pdf("known"), CancellationToken.None, "Example Bank");
        Assert.Null(Assert.Single(await DocumentsAsync()).Institution);
    }

    [Fact]
    public async Task Documents_of_an_unknown_source_are_skipped()
    {
        var pdf = Pdf("nowhere");
        Assert.Empty(await _statements.WantedAsync("unknown-source", [new MoneyMoneyStatementRef(1, BankStatementStore.HashOf(pdf))], CancellationToken.None));
        Assert.Equal(MoneyMoneyStatementOutcome.NoAccount, await _statements.StoreAsync("unknown-source", 1, new DateOnly(2026, 1, 1), "X", pdf, CancellationToken.None, "Example Bank"));
        Assert.Empty(await DocumentsAsync());
    }
}
