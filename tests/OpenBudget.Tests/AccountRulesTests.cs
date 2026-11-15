using Microsoft.EntityFrameworkCore;
using OpenBudget.Assist;
using OpenBudget.Connectors;
using OpenBudget.Migrations;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Account types, the depot rule, account roles and their backfill.</summary>
public sealed class AccountRulesTests
{
    private const string Owner = TestDb.DefaultOwner;

    private static Account Make(long id, long? type, long? portfolio = 0, AccountRole role = AccountRole.None, string name = "Example") =>
        new() { Id = id, OwnerUserId = Owner, Name = name, Type = type, Portfolio = portfolio, Role = role };

    [Fact]
    public async Task WhereDepot_matches_the_depot_type_or_the_portfolio_flag()
    {
        await using var t = new TestDb();
        t.Db.Accounts.AddRange(
            Make(1, (long)AccountType.Giro),
            Make(2, (long)AccountType.Depot),
            Make(3, (long)AccountType.Other, portfolio: 1),
            Make(4, null, portfolio: null));
        await t.Db.SaveChangesAsync();

        var depots = await t.Db.Accounts.AsNoTracking().WhereDepot().OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
        var others = await t.Db.Accounts.AsNoTracking().WhereNotDepot().OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();

        Assert.Equal([2L, 3L], depots);
        Assert.Equal([1L, 4L], others);
        var all = await t.Db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([false, true, true, false], all.Select(a => a.IsDepot));
    }

    [Theory]
    [InlineData(0L, AccountType.Other)]
    [InlineData(1L, AccountType.Giro)]
    [InlineData(2L, AccountType.Savings)]
    [InlineData(3L, AccountType.CreditCard)]
    [InlineData(4L, AccountType.Loan)]
    [InlineData(5L, AccountType.Manual)]
    [InlineData(6L, AccountType.Cash)]
    [InlineData(7L, AccountType.Depot)]
    [InlineData(8L, AccountType.Group)]
    public void Stored_type_codes_map_to_the_enum(long stored, AccountType kind)
    {
        Assert.Equal(kind, Make(1, stored).Kind);
        Assert.Equal(stored, (long)kind);
    }

    [Theory]
    [InlineData(RemoteAccountType.Giro, AccountType.Giro)]
    [InlineData(RemoteAccountType.Savings, AccountType.Savings)]
    [InlineData(RemoteAccountType.CreditCard, AccountType.CreditCard)]
    [InlineData(RemoteAccountType.Depot, AccountType.Depot)]
    [InlineData(RemoteAccountType.Loan, AccountType.Loan)]
    [InlineData(RemoteAccountType.Other, AccountType.Other)]
    public void Remote_types_map_to_local_types(RemoteAccountType remote, AccountType local) =>
        Assert.Equal(local, BankLoginService.LocalType(remote));

    [Fact]
    public void Icon_of_an_account_uses_the_depot_rule()
    {
        Assert.Equal("bi-graph-up-arrow", AccountIcons.ForAccount(Make(1, (long)AccountType.Other, portfolio: 1)));
        Assert.Equal("bi-bank", AccountIcons.ForAccount(Make(2, (long)AccountType.Giro)));
        Assert.Equal("bi-house", AccountIcons.ForAccount(new Account { Type = (long)AccountType.Giro, Icon = "bi-house" }));
    }

    [Fact]
    public async Task Backfill_sets_the_role_from_the_connector_login()
    {
        await using var t = new TestDb();
        t.Db.Accounts.AddRange(
            Make(1, (long)AccountType.Other, name: "Wallet EUR"),
            Make(2, (long)AccountType.Depot, portfolio: 1, name: "Broker A"),
            Make(3, (long)AccountType.Depot, portfolio: 1, name: "Broker B Depot"),
            Make(4, (long)AccountType.Giro, name: "Broker B Cash"),
            Make(5, (long)AccountType.Giro, name: "Example Giro"),
            Make(6, (long)AccountType.Other, name: "PayPal"),
            Make(7, (long)AccountType.Giro, role: AccountRole.PaymentWallet, name: "Set by hand"));
        t.Db.BankLogins.AddRange(
            new BankLogin { Id = 1, OwnerUserId = Owner, ConnectorType = "paypal", DisplayName = "Wallet" },
            new BankLogin { Id = 2, OwnerUserId = Owner, ConnectorType = "kraken", DisplayName = "Broker A" },
            new BankLogin { Id = 3, OwnerUserId = Owner, ConnectorType = "scalable", DisplayName = "Broker B" },
            new BankLogin { Id = 4, OwnerUserId = Owner, ConnectorType = "fints", DisplayName = "Bank" });
        await t.Db.SaveChangesAsync();
        t.Db.BankLoginAccounts.AddRange(
            new BankLoginAccount { BankLoginId = 1, RemoteId = "w", LinkedAccountId = 1 },
            new BankLoginAccount { BankLoginId = 2, RemoteId = "a", LinkedAccountId = 2 },
            new BankLoginAccount { BankLoginId = 3, RemoteId = "d", LinkedAccountId = 3 },
            new BankLoginAccount { BankLoginId = 3, RemoteId = "c", LinkedAccountId = 4 },
            new BankLoginAccount { BankLoginId = 4, RemoteId = "g", LinkedAccountId = 5 },
            new BankLoginAccount { BankLoginId = 4, RemoteId = "h", LinkedAccountId = 7 });
        await t.Db.SaveChangesAsync();

        await t.Db.Database.ExecuteSqlRawAsync(_20261115100500_AccountRole.BackfillSql);

        var roles = await t.Db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Role).ToListAsync();
        Assert.Equal(
            [AccountRole.PaymentWallet, AccountRole.BrokerCash, AccountRole.None, AccountRole.BrokerCash, AccountRole.None, AccountRole.None, AccountRole.PaymentWallet],
            roles);
    }

    [Fact]
    public void Category_evidence_describes_the_account_by_role_then_type()
    {
        Assert.Equal("payment-service wallet", CategoryEvidence.RoleText(Make(1, (long)AccountType.Other, role: AccountRole.PaymentWallet)));
        Assert.Equal("broker cash account", CategoryEvidence.RoleText(Make(2, (long)AccountType.Giro, role: AccountRole.BrokerCash)));
        Assert.Equal("checking account", CategoryEvidence.RoleText(Make(3, (long)AccountType.Giro)));
        // A name alone gives no role: the owner sets it in the account settings.
        Assert.Equal("other account", CategoryEvidence.RoleText(Make(4, (long)AccountType.Other, name: "PayPal")));
    }
}
