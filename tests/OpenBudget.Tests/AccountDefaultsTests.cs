using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Kraken;
using OpenBudget.Connectors.PayPal;
using OpenBudget.Connectors.Scalable;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>An account's role and "In cashflow" flags follow its connector and type until its owner sets them.</summary>
public class AccountDefaultsTests
{
    private static readonly ConnectorRegistry Registry = new([
        FakeBankConnector.Registration,
        PayPalConnector.CreateRegistration(new PayPalOptions()),
        ScalableConnector.CreateRegistration(new ScalableOptions()),
        KrakenConnector.CreateRegistration(new KrakenOptions()),
        BaaderConnector.Registration(new FinTsOptions(null, "test")),
    ]);

    [Theory]
    [InlineData("paypal", RemoteAccountType.Other, AccountRole.PaymentWallet)]
    [InlineData("scalable", RemoteAccountType.Giro, AccountRole.BrokerCash)]
    [InlineData("scalable", RemoteAccountType.Depot, AccountRole.None)]
    [InlineData("kraken", RemoteAccountType.Depot, AccountRole.BrokerCash)]
    [InlineData("baader", RemoteAccountType.Giro, AccountRole.BrokerCash)]
    [InlineData("baader", RemoteAccountType.Depot, AccountRole.None)]
    [InlineData("fake", RemoteAccountType.Giro, AccountRole.None)]
    public void Connectors_give_their_accounts_a_role(string connector, RemoteAccountType type, AccountRole expected) =>
        Assert.Equal(expected, Registry.Find(connector)!.RoleFor(type));

    [Theory]
    [InlineData(AccountType.Giro, AccountRole.None, true)]
    [InlineData(AccountType.CreditCard, AccountRole.None, true)]
    [InlineData(AccountType.Cash, AccountRole.None, true)]
    [InlineData(AccountType.Other, AccountRole.PaymentWallet, true)]
    [InlineData(AccountType.Giro, AccountRole.BrokerCash, false)]
    [InlineData(AccountType.Savings, AccountRole.None, false)]
    [InlineData(AccountType.Depot, AccountRole.None, false)]
    [InlineData(AccountType.Other, AccountRole.None, false)]
    public void In_cashflow_follows_type_and_role(AccountType type, AccountRole role, bool expected) =>
        Assert.Equal(expected, AccountAssignmentDefaults.InCashflow(new Account { Type = (long)type, Role = role }));

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(1, "PayPal").Type = (long)AccountType.Other;
        t.AddAccount(2, "Broker cash").Type = (long)AccountType.Giro;
        t.AddAccount(3, "Giro").Type = (long)AccountType.Giro;
        t.AddAccount(4, "Savings").Type = (long)AccountType.Savings;
        t.Db.BankLogins.AddRange(
            new BankLogin { Id = 1, OwnerUserId = TestDb.DefaultOwner, ConnectorType = "paypal", DisplayName = "Wallet" },
            new BankLogin { Id = 2, OwnerUserId = TestDb.DefaultOwner, ConnectorType = "scalable", DisplayName = "Broker" });
        await t.Db.SaveChangesAsync();
        t.Db.BankLoginAccounts.AddRange(
            new BankLoginAccount { BankLoginId = 1, RemoteId = "w", Type = RemoteAccountType.Other, LinkedAccountId = 1 },
            new BankLoginAccount { BankLoginId = 2, RemoteId = "c", Type = RemoteAccountType.Giro, LinkedAccountId = 2 });
        await t.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Data_fix_keeps_choices_made_by_hand_and_derives_the_rest()
    {
        await using var t = await WorldAsync();
        var accounts = await t.Db.Accounts.ToDictionaryAsync(a => a.Id);
        accounts[3].Role = AccountRole.PaymentWallet;   // set by hand: no connector says so
        t.Db.ProjectAccountAssignments.AddRange(
            new ProjectAccountAssignment { ProjectId = 1, AccountId = 1, InCashflow = true },   // the default for a wallet
            new ProjectAccountAssignment { ProjectId = 1, AccountId = 2, InCashflow = true },   // differs from broker cash's default
            new ProjectAccountAssignment { ProjectId = 1, AccountId = 3, InCashflow = false },  // differs from a giro's default
            new ProjectAccountAssignment { ProjectId = 1, AccountId = 4, InCashflow = true });  // savings: differs from the default
        await t.Db.SaveChangesAsync();

        var (roles, flags) = await AccountDefaults.BackfillAsync(t.Db, Registry);

        Assert.Equal((1, 3), (roles, flags));
        t.Db.ChangeTracker.Clear();
        var after = await t.Db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        Assert.Equal((AccountRole.PaymentWallet, false), (after[1].Role, after[1].RoleByUser));
        Assert.Equal((AccountRole.BrokerCash, false), (after[2].Role, after[2].RoleByUser));
        Assert.Equal((AccountRole.PaymentWallet, true), (after[3].Role, after[3].RoleByUser));
        // no flag changes: those off the default become the owner's
        var cashflow = await t.Db.ProjectAccountAssignments.AsNoTracking().ToDictionaryAsync(a => a.AccountId);
        Assert.Equal((true, false), (cashflow[1].InCashflow, cashflow[1].InCashflowByUser));
        Assert.Equal((true, true), (cashflow[2].InCashflow, cashflow[2].InCashflowByUser));
        Assert.Equal((false, true), (cashflow[3].InCashflow, cashflow[3].InCashflowByUser));
        Assert.Equal((true, true), (cashflow[4].InCashflow, cashflow[4].InCashflowByUser));
    }

    [Fact]
    public async Task A_derived_role_never_overwrites_the_owners_choice()
    {
        await using var t = await WorldAsync();
        var account = await t.Db.Accounts.FirstAsync(a => a.Id == 2);
        account.Role = AccountRole.None;
        account.RoleByUser = true;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 2, InCashflow = true });
        await t.Db.SaveChangesAsync();

        await AccountDefaults.ApplyRoleAsync(t.Db, account, AccountRole.BrokerCash);
        Assert.Equal(AccountRole.None, account.Role);

        account.RoleByUser = false;
        await AccountDefaults.ApplyRoleAsync(t.Db, account, AccountRole.BrokerCash);
        Assert.Equal(AccountRole.BrokerCash, account.Role);
        // the flag nobody set follows the role
        Assert.False((await t.Db.ProjectAccountAssignments.SingleAsync()).InCashflow);
    }

    [Fact]
    public async Task Owner_overrides_and_resets_role_and_cashflow()
    {
        await using var t = await WorldAsync();
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 2, InCashflow = false });
        (await t.Db.Accounts.FirstAsync(a => a.Id == 2)).Role = AccountRole.BrokerCash;
        await t.Db.SaveChangesAsync();
        var structure = new StructureService(t.System, t.System, t.Notifier, new ChangeOrigin(), registry: Registry);

        await structure.SetAccountRoleAsync(2, AccountRole.None);
        var info = await structure.GetAccountRoleAsync(2);
        Assert.Equal(new AccountRoleInfo(AccountRole.None, true, AccountRole.BrokerCash, "Scalable Capital"), info);
        Assert.True(await InCashflowAsync(t));   // a regular giro counts

        await t.Spending.SetAccountInCashflowAsync(1, 2, false);
        await structure.SetAccountRoleAsync(2, null);
        Assert.Equal(new AccountRoleInfo(AccountRole.BrokerCash, false, AccountRole.BrokerCash, "Scalable Capital"), await structure.GetAccountRoleAsync(2));
        Assert.False(await InCashflowAsync(t));

        await t.Spending.SetAccountInCashflowAsync(1, 2, true);
        await structure.SetAccountRoleAsync(2, AccountRole.BrokerCash);
        Assert.True(await InCashflowAsync(t));   // the owner's tick stays
        await t.Spending.SetAccountInCashflowAsync(1, 2, null);
        Assert.False(await InCashflowAsync(t));
    }

    private static async Task<bool> InCashflowAsync(TestDb t)
    {
        await using var db = await t.Factory.CreateDbContextAsync();
        return (await db.ProjectAccountAssignments.SingleAsync()).InCashflow;
    }
}
