using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;

namespace OpenBudget.Services;

/// <summary>One file of a statements ZIP: where it is on disk and its (unique) name in the archive.</summary>
public sealed record StatementArchiveEntry(string Path, string Name);

/// <summary>
/// Several statements as one ZIP (the Documents view's multi-select download). The statements are looked up
/// through a context the caller scoped to the signed-in user, so exactly the access rules of the single download
/// apply: ids the user cannot see (or whose file is missing) are skipped.
/// </summary>
public static class StatementArchive
{
    /// <summary>The visible statements among <paramref name="ids"/>, in the given order, with unique entry names.</summary>
    public static async Task<List<StatementArchiveEntry>> ResolveAsync(BudgetDbContext db, BankStatementStore store, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        var rows = await db.BankStatements.AsNoTracking()
            .Where(s => wanted.Contains(s.Id))
            .Select(s => new { s.Id, s.AccountId, s.BankLoginId, s.Date, s.Name, s.FilePath })
            .ToListAsync(ct);
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "", ct);
        var loginIds = rows.Where(r => r.AccountId == null).Select(r => r.BankLoginId).Distinct().ToList();
        var logins = await db.BankLogins.AsNoTracking().Where(l => loginIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);

        var byId = rows.ToDictionary(r => r.Id);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StatementArchiveEntry>();
        foreach (var id in wanted)
        {
            if (!byId.TryGetValue(id, out var r)) continue;
            string path;
            try { path = store.Resolve(r.FilePath); }
            catch (InvalidOperationException) { continue; }
            if (!File.Exists(path)) continue;
            var owner = r.AccountId is { } a ? accounts.GetValueOrDefault(a, "") : logins.GetValueOrDefault(r.BankLoginId, "");
            result.Add(new StatementArchiveEntry(path, Unique(used, FileName(r.Date, owner, r.Name))));
        }
        return result;
    }

    /// <summary>"{yyyy-MM-dd} {account} {title}" without characters that are unsafe in file names (no extension).</summary>
    public static string FileName(DateTime date, string account, string title)
    {
        var raw = $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {account} {title}";
        var sb = new StringBuilder(raw.Length);
        var space = false;
        foreach (var c in raw)
        {
            var bad = char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
            if (bad || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        var name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length > 150) name = name[..150].TrimEnd();
        return name.Length == 0 ? "statement" : name;
    }

    private static string Unique(HashSet<string> used, string stem)
    {
        var name = stem + ".pdf";
        var n = 1;
        while (!used.Add(name))
        {
            n++;
            name = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}).pdf";
        }
        return name;
    }

    /// <summary>Writes the entries as a ZIP to a (non-seekable) stream, one file at a time. PDFs are already compressed: stored as is.</summary>
    public static async Task WriteAsync(Stream output, IEnumerable<StatementArchiveEntry> entries, CancellationToken ct)
    {
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct);
        foreach (var e in entries)
        {
            var entry = zip.CreateEntry(e.Name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(e.Path));
            await using var target = await entry.OpenAsync(ct);
            await using var source = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await source.CopyToAsync(target, ct);
        }
    }
}
