using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>A statement MoneyMoney holds: its account (MoneyMoney rowid) and the SHA-256 of its file.</summary>
public sealed record MoneyMoneyStatementRef(long AccountKey, string Sha256);

/// <summary>One row of MoneyMoney's <c>statements</c> table: the file lives at Statements/&lt;Directory&gt;/&lt;Filename&gt;.</summary>
public sealed record MoneyMoneyStatementRow(long AccountKey, string Name, string Directory, string Filename, long CreatedTimestamp);

/// <summary>
/// Result of <see cref="MoneyMoneyStatementImport.ImportFolderAsync"/>: statements MoneyMoney lists, those whose file
/// was found, those stored, and those the owner already had.
/// </summary>
public sealed record MoneyMoneyStatementSummary(int Listed, int Found, int Stored, int AlreadyKept);

public enum MoneyMoneyStatementOutcome
{
    Stored,

    /// <summary>The owner already has a document with the same content (from a bank connector or an earlier upload).</summary>
    Duplicate,

    /// <summary>The source is unknown or has no owner yet.</summary>
    NoAccount,
}

/// <summary>
/// Imports the statement PDFs MoneyMoney stored into the documents (<c>bank_statements</c>) of the source's
/// accounts. Documents of accounts OpenBudget does not know (e.g. accounts removed in MoneyMoney) are kept
/// without an account, labelled with MoneyMoney's bank folder, for the owner to assign later. A document is kept
/// once per owner and content: one a bank connector already downloaded is not imported again, and repeated
/// uploads store nothing new.
/// </summary>
public sealed class MoneyMoneyStatementImport
{
    /// <summary>Prefix of <see cref="Models.BankStatement.Identifier"/> for imported documents (followed by the content hash).</summary>
    public const string IdentifierPrefix = "mm:";

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly BankStatementStore _store;
    private readonly MoneyMoneyImportService _importer;
    private readonly ChangeNotificationService _notifier;

    public MoneyMoneyStatementImport(IDbContextFactory<BudgetDbContext> dbFactory, BankStatementStore store, MoneyMoneyImportService importer, ChangeNotificationService notifier)
    {
        _dbFactory = dbFactory;
        _store = store;
        _importer = importer;
        _notifier = notifier;
    }

    /// <summary>The hashes of <paramref name="statements"/> the server still needs (content not stored for the owner yet).</summary>
    public async Task<List<string>> WantedAsync(string sourceName, IReadOnlyCollection<MoneyMoneyStatementRef> statements, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await db.ImportSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == sourceName, ct);
        if (source?.OwnerUserId is not { } owner) return [];
        var stored = (await db.BankStatements.AsNoTracking().Where(s => s.OwnerUserId == owner).Select(s => s.Sha256).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return statements
            .Where(s => !stored.Contains(s.Sha256))
            .Select(s => s.Sha256.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Stores one statement under the account MoneyMoney filed it with (or, when that account is not in OpenBudget,
    /// without an account, labelled <paramref name="bank"/>), unless the owner already has its content.
    /// </summary>
    public Task<MoneyMoneyStatementOutcome> StoreAsync(string sourceName, long accountKey, DateOnly date, string name, byte[] content, CancellationToken ct, string? bank = null) =>
        _importer.WithSourceLockAsync(sourceName, async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var source = await db.ImportSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == sourceName, ct);
            if (source?.OwnerUserId is not { } owner) return MoneyMoneyStatementOutcome.NoAccount;
            var accountId = SourceScope.Namespace(source.Id, accountKey);
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.SourceId == source.Id, ct);

            var hash = BankStatementStore.HashOf(content);
            if (await db.BankStatements.AnyAsync(s => s.OwnerUserId == owner && s.Sha256 == hash, ct))
            {
                // a copy of the same date without an account takes over MoneyMoney's account
                if (account != null)
                    await BankStatementStore.MergeIntoExistingAsync(db, owner, null, account, IdentifierPrefix + hash, hash, date, ct);
                return MoneyMoneyStatementOutcome.Duplicate;
            }

            var title = string.IsNullOrWhiteSpace(name) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : name.Trim();
            if (title.Length > 300) title = title[..300];
            await _store.StoreAsync(db, owner, null, "moneymoney-" + source.Id.ToString(CultureInfo.InvariantCulture), account,
                new RemoteStatement(IdentifierPrefix + hash, null, date, title, content), ct,
                account == null ? Label(bank) : null);
            _notifier.Publish(new DataChange(DataArea.BankSync, UserId: owner));
            return MoneyMoneyStatementOutcome.Stored;
        }, ct);

    /// <summary>
    /// Imports the statements MoneyMoney lists in its <c>statements</c> table (not deleted ones) from the files under
    /// <paramref name="statementsRoot"/> (<c>&lt;directory&gt;/&lt;filename&gt;</c>), through <see cref="WantedAsync"/> and
    /// <see cref="StoreAsync"/>. Files that are missing, outside the folder or not PDFs are skipped.
    /// <paramref name="progress"/> gets (done, total) after each statement.
    /// </summary>
    public async Task<MoneyMoneyStatementSummary> ImportFolderAsync(string sourceName, string plainDatabasePath, string statementsRoot,
        Action<int, int>? progress, CancellationToken ct)
    {
        var rows = await ReadStatementRowsAsync(plainDatabasePath, ct);
        var root = Path.GetFullPath(statementsRoot);
        var files = new List<(MoneyMoneyStatementRow Row, string Path, string Sha256)>();
        foreach (var row in rows)
        {
            var path = Path.GetFullPath(Path.Combine(root, row.Directory, row.Filename));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
            files.Add((row, path, hash));
        }
        var wanted = (await WantedAsync(sourceName, files.Select(f => new MoneyMoneyStatementRef(f.Row.AccountKey, f.Sha256)).ToList(), ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int stored = 0, duplicates = 0, done = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        progress?.Invoke(0, files.Count);
        foreach (var (row, path, hash) in files)
        {
            if (wanted.Contains(hash) && seen.Add(hash))
            {
                var content = await File.ReadAllBytesAsync(path, ct);
                if (content.Length >= 5 && content.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                {
                    var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(row.CreatedTimestamp).UtcDateTime);
                    var name = string.IsNullOrWhiteSpace(row.Name) ? Path.GetFileNameWithoutExtension(row.Filename) : row.Name;
                    var outcome = await StoreAsync(sourceName, row.AccountKey, date, name, content, ct, row.Directory);
                    if (outcome == MoneyMoneyStatementOutcome.Stored) stored++;
                    else if (outcome == MoneyMoneyStatementOutcome.Duplicate) duplicates++;
                }
            }
            else
            {
                duplicates++;
            }
            progress?.Invoke(++done, files.Count);
        }
        return new MoneyMoneyStatementSummary(rows.Count, files.Count, stored, duplicates);
    }

    /// <summary>The not deleted rows of MoneyMoney's <c>statements</c> table; empty when the table does not exist.</summary>
    private static async Task<List<MoneyMoneyStatementRow>> ReadStatementRowsAsync(string plainDatabasePath, CancellationToken ct)
    {
        var rows = new List<MoneyMoneyStatementRow>();
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = plainDatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connection);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'statements'";
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) return rows;
        cmd.CommandText = "SELECT account_key, name, directory, filename, created_tsp FROM statements "
            + "WHERE account_key IS NOT NULL AND filename IS NOT NULL AND COALESCE(deleted, 0) = 0";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MoneyMoneyStatementRow(
                reader.GetInt64(0),
                await reader.IsDBNullAsync(1, ct) ? "" : reader.GetString(1),
                await reader.IsDBNullAsync(2, ct) ? "" : reader.GetString(2),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, ct) ? 0 : reader.GetInt64(4)));
        }
        return rows;
    }

    private static string Label(string? bank)
    {
        var label = string.IsNullOrWhiteSpace(bank) ? "MoneyMoney" : bank.Trim();
        return label.Length > 200 ? label[..200] : label;
    }
}
