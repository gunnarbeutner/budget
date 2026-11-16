using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// An account of the demo project that receipts are paid from, so they show in the project's receipt list: the given
/// sources are mapped to it, and an Amazon login's card (<see cref="AmazonCard"/>) is mapped to it.
/// </summary>
internal static class ReceiptsSeed
{
    public const string AmazonCard = "visa-0000";
    public static readonly long AccountId = RowIds.Namespace(E2ESeed.IdSpace, 900);
    public const string ReceiptsPath = "/projects/1/receipts";

    /// <summary>Adds the account and the mappings; returns the Amazon login's id.</summary>
    public static async Task<int> SeedAsync(IDbContextFactory<BudgetDbContext> factory, params string[] sources)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Accounts.Add(new Account
        {
            Id = AccountId,
            OwnerUserId = E2ESeed.OwnerUserId,
            Name = "Example Card Account",
            Provider = AccountProviders.Manual,
            Type = 1,
            Currency = "EUR",
            Balances = "11:0.000000,3:EUR,",
        });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = AccountId });
        foreach (var s in sources)
            db.ReceiptSourceAccounts.Add(new ReceiptSourceAccount { OwnerUserId = E2ESeed.OwnerUserId, Source = s, AccountId = AccountId });
        var login = new BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "amazon", DisplayName = "Example Shop", RefreshInterval = RefreshInterval.ManualOnly };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = AmazonCard, Name = "Visa •••• 0000", EnrichAccountId = AccountId });
        await db.SaveChangesAsync();
        return login.Id;
    }
}
