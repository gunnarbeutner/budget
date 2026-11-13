using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>What <see cref="DocumentDedupe"/> found (dry run) or did.</summary>
public sealed record DocumentDedupeResult(int Sets, int Deleted, int AccountsCarried, int AliasesAdded, int FilesDeleted);

/// <summary>
/// Folds duplicate documents into one: rows of the same owner with the same content (<see cref="BankStatement.Sha256"/>)
/// and the same calendar date. Documents with the same content on different dates (banks resend identical notices)
/// and copies owned by different users stay apart.
/// </summary>
/// <remarks>
/// Per set the survivor is the bank-login copy with the lowest id, else the lowest id. It takes over the first account
/// of the others when it has none; read states and aliases move to it; the others' bank identifiers become aliases
/// so syncs do not download them again. All of it happens in one transaction; files no remaining row uses are
/// deleted after the commit (missing files are fine). A second run finds nothing to do.
/// </remarks>
public static class DocumentDedupe
{
    public static async Task<DocumentDedupeResult> RunAsync(BudgetDbContext db, BankStatementStore store, bool apply, IList<string> report, CancellationToken ct = default)
    {
        var rows = await db.BankStatements.OrderBy(s => s.Id).ToListAsync(ct);
        var sets = rows
            .GroupBy(s => (s.OwnerUserId, Sha: s.Sha256.ToUpperInvariant(), Day: s.Date.Date))
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(s => s.BankLoginId == null ? 1 : 0).ThenBy(s => s.Id).ToList())
            .ToList();
        var userNames = await db.Users.ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id, StringComparer.Ordinal, ct);
        var accountNames = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        string AccountLabel(long? id) => id is { } a ? $"{(accountNames.TryGetValue(a, out var n) ? n : "?")} (#{a})" : "none";

        report.Add(apply ? "Removing duplicate documents." : "Dry run (pass --apply to change anything).");
        await using var tx = apply ? await db.Database.BeginTransactionAsync(ct) : null;
        var existingAliases = (await db.BankStatementAliases.Select(a => new { a.BankLoginId, a.Identifier }).ToListAsync(ct))
            .Select(a => (a.BankLoginId, a.Identifier)).ToHashSet();
        int deleted = 0, carried = 0, aliasesAdded = 0;
        var deletedIds = new List<long>();

        foreach (var owner in sets.GroupBy(s => s[0].OwnerUserId, StringComparer.Ordinal).OrderBy(g => userNames.GetValueOrDefault(g.Key, g.Key), StringComparer.Ordinal))
        {
            var ownerDeleted = owner.Sum(s => s.Count - 1);
            var ownerCarried = 0;
            var lines = new List<string>();
            foreach (var set in owner)
            {
                var survivor = set[0];
                var others = set.Skip(1).ToList();
                var line = $"    keep #{survivor.Id} \"{survivor.Name}\" {survivor.Date:yyyy-MM-dd} (account {AccountLabel(survivor.AccountId)})"
                    + $", delete {string.Join(", ", others.Select(o => $"#{o.Id}"))}";
                if (survivor.AccountId == null && others.FirstOrDefault(o => o.AccountId != null) is { } withAccount)
                {
                    line += $"; account {AccountLabel(withAccount.AccountId)} carried over";
                    ownerCarried++;
                    survivor.AccountId = withAccount.AccountId;
                    survivor.OwnerUserId = withAccount.OwnerUserId;
                    survivor.Institution = null;
                }
                lines.Add(line);
                foreach (var other in others)
                {
                    if (other.BankLoginId is { } login && existingAliases.Add((login, other.Identifier)))
                    {
                        db.BankStatementAliases.Add(new BankStatementAlias { StatementId = survivor.Id, BankLoginId = login, Identifier = other.Identifier });
                        aliasesAdded++;
                    }
                    deletedIds.Add(other.Id);
                    if (!apply) continue;
                    // aliases already pointing at the deleted row follow it to the survivor
                    await db.BankStatementAliases.Where(a => a.StatementId == other.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(a => a.StatementId, survivor.Id), ct);
                    // read states: a user's state on the survivor wins; otherwise the deleted copy's moves over
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO statement_reads (UserId, StatementId, ReadAt, Unread)
                        SELECT UserId, {survivor.Id}, ReadAt, Unread FROM statement_reads r
                        WHERE r.StatementId = {other.Id}
                          AND NOT EXISTS (SELECT 1 FROM statement_reads s WHERE s.StatementId = {survivor.Id} AND s.UserId = r.UserId)
                        """, ct);
                }
            }
            deleted += ownerDeleted;
            carried += ownerCarried;
            report.Add(string.Create(CultureInfo.InvariantCulture,
                $"{userNames.GetValueOrDefault(owner.Key, owner.Key)}: {owner.Count()} sets, {ownerDeleted} rows to delete, {ownerCarried} account assignments carried over"));
            foreach (var l in lines) report.Add(l);
        }

        var filesDeleted = 0;
        if (apply)
        {
            db.BankStatements.RemoveRange(rows.Where(r => deletedIds.Contains(r.Id)));
            await db.SaveChangesAsync(ct);
            await tx!.CommitAsync(ct);
            var gone = rows.Where(r => deletedIds.Contains(r.Id)).Select(r => r.FilePath).ToHashSet(StringComparer.Ordinal);
            var used = await db.BankStatements.Select(s => s.FilePath).ToListAsync(ct);
            gone.ExceptWith(used);
            foreach (var path in gone)
            {
                if (File.Exists(store.Resolve(path))) filesDeleted++;
                store.DeleteFile(path);
            }
        }
        else
        {
            db.ChangeTracker.Clear();
        }

        var files = apply ? string.Create(CultureInfo.InvariantCulture, $", {filesDeleted} files deleted.") : ".";
        report.Add(string.Create(CultureInfo.InvariantCulture,
            $"Total: {sets.Count} sets, {deleted} rows {(apply ? "deleted" : "to delete")}, {carried} account assignments carried over, {aliasesAdded} bank identifiers {(apply ? "recorded" : "to record")} as aliases{files}"));
        return new DocumentDedupeResult(sets.Count, deleted, carried, aliasesAdded, filesDeleted);
    }
}
