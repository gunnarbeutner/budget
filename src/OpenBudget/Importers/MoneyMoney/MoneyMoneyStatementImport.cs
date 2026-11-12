using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>A statement MoneyMoney holds: its account (MoneyMoney rowid) and the SHA-256 of its file.</summary>
public sealed record MoneyMoneyStatementRef(long AccountKey, string Sha256);

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

    private static string Label(string? bank)
    {
        var label = string.IsNullOrWhiteSpace(bank) ? "MoneyMoney" : bank.Trim();
        return label.Length > 200 ? label[..200] : label;
    }
}
