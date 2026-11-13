using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>Where statement files live (on the data volume): &lt;root&gt;/&lt;owner&gt;/&lt;login&gt;/&lt;file&gt;.</summary>
public sealed record BankStorageOptions(string StatementsRoot);

/// <summary>Stores downloaded documents as files and indexes them in <c>bank_statements</c>.</summary>
public sealed class BankStatementStore
{
    private readonly BankStorageOptions _options;

    public BankStatementStore(BankStorageOptions options) => _options = options;

    /// <summary>Writes the document and its index row. <paramref name="account"/> is null for documents of the login as a whole.</summary>
    public Task<BankStatement> StoreAsync(BudgetDbContext db, BankLogin login, Account? account, RemoteStatement statement, CancellationToken ct) =>
        StoreAsync(db, login.OwnerUserId, login.Id, login.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), account, statement, ct);

    /// <summary>
    /// Writes the document and its index row for <paramref name="ownerUserId"/>; <paramref name="loginId"/> is null for
    /// documents that came from no bank login. <paramref name="folder"/> is the subfolder below the owner's directory;
    /// <paramref name="institution"/> labels a document stored without an account.
    /// </summary>
    /// <remarks>
    /// A document is kept once per owner, content and date: when the owner already has it, nothing is written and the
    /// existing row is returned (see <see cref="MergeIntoExistingAsync"/>).
    /// </remarks>
    public async Task<BankStatement> StoreAsync(BudgetDbContext db, string ownerUserId, int? loginId, string folder, Account? account, RemoteStatement statement, CancellationToken ct, string? institution = null)
    {
        var existing = await MergeIntoExistingAsync(db, ownerUserId, loginId, account, statement.Identifier, HashOf(statement.Content), statement.Date, ct);
        if (existing != null) return existing;

        var relative = Path.Combine(ownerUserId, folder, $"{Guid.NewGuid():N}.pdf");
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, statement.Content, ct);
        try
        {
            var row = new BankStatement
            {
                OwnerUserId = ownerUserId,
                BankLoginId = loginId,
                AccountId = account?.Id,
                Identifier = statement.Identifier,
                Date = DateTime.SpecifyKind(statement.Date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                Name = statement.Name,
                FilePath = relative.Replace('\\', '/'),
                Sha256 = HashOf(statement.Content),
                SizeBytes = statement.Content.LongLength,
                Institution = institution,
            };
            db.BankStatements.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
        catch
        {
            File.Delete(full);
            throw;
        }
    }

    /// <summary>
    /// Finds the owner's document with this content and date and folds the arriving copy into it: an account the
    /// existing row lacks is taken over; a copy from a bank login makes a MoneyMoney-imported row that login's document
    /// (so the sync tracks it) or, when the row already belongs to a login, is recorded as a
    /// <see cref="BankStatementAlias"/> so the identifier is not downloaded again. Returns null when there is no such document.
    /// </summary>
    public static async Task<BankStatement?> MergeIntoExistingAsync(BudgetDbContext db, string ownerUserId, int? loginId, Account? account, string identifier, string sha256, DateOnly date, CancellationToken ct)
    {
        var from = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var to = from.AddDays(1);
        var hash = sha256.ToUpperInvariant();
        var existing = await db.BankStatements
            .Where(s => s.OwnerUserId == ownerUserId && s.Sha256 == hash && s.Date >= from && s.Date < to)
            .OrderBy(s => s.BankLoginId == null ? 1 : 0).ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (existing == null) return null;

        if (account != null && existing.AccountId == null)
        {
            existing.AccountId = account.Id;
            existing.OwnerUserId = account.OwnerUserId;
            existing.Institution = null;
        }
        if (loginId is { } login)
        {
            if (existing.BankLoginId == null)
            {
                existing.BankLoginId = login;
                existing.Identifier = identifier;
            }
            else if ((existing.BankLoginId != login || !string.Equals(existing.Identifier, identifier, StringComparison.Ordinal))
                     && !await db.BankStatementAliases.AnyAsync(a => a.BankLoginId == login && a.Identifier == identifier, ct))
            {
                db.BankStatementAliases.Add(new BankStatementAlias { StatementId = existing.Id, BankLoginId = login, Identifier = identifier });
            }
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>The content hash stored in <see cref="BankStatement.Sha256"/> (upper-case hex).</summary>
    public static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>The absolute path of a stored file; refuses anything that escapes the statements root.</summary>
    public string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(_options.StatementsRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Statement path escapes the statements directory.");
        return full;
    }

    public void DeleteFile(string relativePath)
    {
        try { File.Delete(Resolve(relativePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* best effort */ }
    }

    /// <summary>Prefix of the entries in the known set that name a requested account (by remote id) already holding documents.</summary>
    public const string AccountHasDocumentsMarker = "@account-has-documents:";

    /// <summary>Identifiers already downloaded for a login, including aliases of kept copies (never downloaded again).</summary>
    public static async Task<HashSet<string>> KnownIdentifiersAsync(BudgetDbContext db, int loginId, CancellationToken ct)
    {
        var known = (await db.BankStatements.Where(s => s.BankLoginId == loginId).Select(s => s.Identifier).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        known.UnionWith(await db.BankStatementAliases.Where(a => a.BankLoginId == loginId).Select(a => a.Identifier).ToListAsync(ct));
        return known;
    }
}
