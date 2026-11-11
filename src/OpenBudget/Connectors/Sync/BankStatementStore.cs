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
    public async Task<BankStatement> StoreAsync(BudgetDbContext db, BankLogin login, Account? account, RemoteStatement statement, CancellationToken ct)
    {
        var relative = Path.Combine(login.OwnerUserId, login.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{Guid.NewGuid():N}.pdf");
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, statement.Content, ct);
        try
        {
            var row = new BankStatement
            {
                OwnerUserId = login.OwnerUserId,
                BankLoginId = login.Id,
                AccountId = account?.Id,
                SourceId = account?.SourceId,
                Identifier = statement.Identifier,
                Date = DateTime.SpecifyKind(statement.Date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                Name = statement.Name,
                FilePath = relative.Replace('\\', '/'),
                Sha256 = Convert.ToHexString(SHA256.HashData(statement.Content)).ToUpperInvariant(),
                SizeBytes = statement.Content.LongLength,
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

    /// <summary>Identifiers already downloaded for a login (never downloaded again).</summary>
    /// <summary>Prefix of the entries in the known set that name a requested account (by remote id) already holding documents.</summary>
    public const string AccountHasDocumentsMarker = "@account-has-documents:";

    public static async Task<HashSet<string>> KnownIdentifiersAsync(BudgetDbContext db, int loginId, CancellationToken ct) =>
        (await db.BankStatements.Where(s => s.BankLoginId == loginId).Select(s => s.Identifier).ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);
}
