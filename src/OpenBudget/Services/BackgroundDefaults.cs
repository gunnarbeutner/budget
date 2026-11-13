using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;

namespace OpenBudget.Services;

/// <summary>
/// Background syncs are on by default for logins that can sync fully on their own. Logins whose default was not settled
/// yet get it once: switched on when they can sync fully in the background, their credentials work and they never ran in
/// the background; every such login is then marked settled, so the owner's later choice stays (idempotent).
/// </summary>
public static class BackgroundDefaults
{
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
}
