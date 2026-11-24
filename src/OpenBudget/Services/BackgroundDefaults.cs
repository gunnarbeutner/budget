using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Whether a login syncs in the background, unless its owner chose (<see cref="BankLogin.BackgroundByUser"/>):
/// <list type="bullet">
/// <item>New logins that can sync fully on their own start with background syncs on.</item>
/// <item>A login that can sync in the background at all switches them on after
/// <see cref="BankSyncOptions.AutoBackgroundAfter"/> successful syncs in a row without a TAN.</item>
/// <item>A background run that stops for the owner (a TAN, an expired session) switches them off again; the login then
/// counts its TAN-free syncs from zero.</item>
/// </list>
/// The owner's own choice, on or off, is never changed by these rules (a rejected password still stops background runs).
/// </summary>
public static class BackgroundDefaults
{
    /// <summary>How a finished sync went, for <see cref="Record"/>.</summary>
    public enum SyncResult
    {
        /// <summary>Finished without asking the owner for anything.</summary>
        Clean,

        /// <summary>The owner answered a TAN, approval or code.</summary>
        AskedTan,

        /// <summary>A background run stopped because it needs the owner.</summary>
        StoppedForOwner,

        /// <summary>Failed for another reason: says nothing about TANs.</summary>
        Failed,
    }

    /// <summary>Applies the automatic rule after a sync; <paramref name="capable"/>: the login's connector can sync in the background.</summary>
    public static void Record(BankLogin login, SyncResult result, bool capable, int autoAfter)
    {
        switch (result)
        {
            case SyncResult.Clean:
                if (login.SyncsWithoutTan < int.MaxValue) login.SyncsWithoutTan++;
                Promote(login, capable, autoAfter);
                break;
            case SyncResult.AskedTan:
                login.SyncsWithoutTan = 0;
                break;
            case SyncResult.StoppedForOwner:
                login.SyncsWithoutTan = 0;
                if (!login.BackgroundByUser) login.BackgroundEnabled = false;
                break;
        }
    }

    /// <summary>Switches background syncs on when the automatic rule says so; returns whether it did.</summary>
    public static bool Promote(BankLogin login, bool capable, int autoAfter)
    {
        if (login.BackgroundByUser || login.BackgroundEnabled || !capable || login.CredentialsInvalid) return false;
        if (login.SyncsWithoutTan < Math.Max(1, autoAfter)) return false;
        login.BackgroundEnabled = true;
        login.BackgroundFailures = 0;
        login.NextBackgroundAttemptAt = null;
        return true;
    }

    public static async Task<int> ApplyAsync(BudgetDbContext db, ConnectorRegistry registry, SecretsProtector secrets, CancellationToken ct = default)
    {
        if (!secrets.IsConfigured) return 0;
        var logins = await db.BankLogins.IgnoreQueryFilters().Where(l => !l.BackgroundDefaulted).ToListAsync(ct);
        if (logins.Count == 0) return 0;
        var ids = logins.Select(l => l.Id).ToList();
        var ran = (await db.BackgroundSyncRuns.IgnoreQueryFilters().Where(r => r.Background && ids.Contains(r.BankLoginId))
            .Select(r => r.BankLoginId).Distinct().ToListAsync(ct)).ToHashSet();
        var switched = 0;
        foreach (var login in logins)
        {
            login.BackgroundDefaulted = true;
            if (login.BackgroundEnabled || login.CredentialsInvalid || ran.Contains(login.Id)) continue;
            var credentials = BankSyncRunner.DecodeCredentials(secrets, login.EncryptedCredentials);
            if (registry.Find(login.ConnectorType)?.SilentCapabilityFor(credentials) != SilentCapability.Full) continue;
            login.BackgroundEnabled = true;
            switched++;
        }
        await db.SaveChangesAsync(ct);
        return switched;
    }

    /// <summary>
    /// Marks the logins whose background setting can only be the owner's choice as chosen by the owner: switched off although
    /// it can sync fully on its own and its credentials work (the defaults would have switched it on), or switched on although
    /// it cannot sync fully on its own (the defaults never do that). All others follow the automatic rule. Returns how many
    /// it marked.
    /// </summary>
    public static async Task<int> MarkOwnerChoicesAsync(BudgetDbContext db, ConnectorRegistry registry, SecretsProtector secrets, CancellationToken ct = default)
    {
        if (!secrets.IsConfigured) return 0;
        var logins = await db.BankLogins.IgnoreQueryFilters().Where(l => !l.BackgroundByUser).ToListAsync(ct);
        var marked = 0;
        foreach (var login in logins)
        {
            var credentials = BankSyncRunner.DecodeCredentials(secrets, login.EncryptedCredentials);
            var full = registry.Find(login.ConnectorType)?.SilentCapabilityFor(credentials) == SilentCapability.Full;
            var chosen = login.BackgroundEnabled ? !full : full && !login.CredentialsInvalid && login.NeedsManualSync == null;
            if (!chosen) continue;
            login.BackgroundByUser = true;
            marked++;
        }
        await db.SaveChangesAsync(ct);
        return marked;
    }
}
