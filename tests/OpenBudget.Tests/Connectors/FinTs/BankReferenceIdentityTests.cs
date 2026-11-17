using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>FinTS bank references are shared between bookings, so they do not identify one.</summary>
public sealed class BankReferenceIdentityTests
{
    private const string Statement =
        ":20:STARTUMS\r\n:25:50010517/0000000001\r\n:28C:0\r\n:60F:C261110EUR100,00\r\n"
        + ":61:2611111111D56,94NDDT//006220\r\n:86:105?00Lastschrift?20Phone bill?32Example Telecom\r\n"
        + ":61:2611161116D55,08NDDT//006220\r\n:86:105?00Lastschrift?20Broadcast fee?32Example Broadcasting\r\n"
        + ":62F:D261116EUR12,02\r\n-";

    [Fact]
    public void A_bank_reference_is_kept_but_not_taken_as_the_bookings_identity()
    {
        var list = Mt940Parser.Parse(Statement, "EUR", booked: true);
        Assert.Equal(2, list.Count);
        Assert.All(list, t => Assert.Equal("006220", t.BankTransactionId));
        Assert.All(list, t => Assert.False(t.IdIsUnique));
    }

    [Fact]
    public void A_camt_servicer_reference_is_not_taken_as_the_bookings_identity_either()
    {
        const string Camt = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.052.001.08"><BkToCstmrAcctRpt><Rpt>
            <Ntry><Amt Ccy="EUR">7.20</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts><BookgDt><Dt>2026-11-16</Dt></BookgDt><AcctSvcrRef>009500</AcctSvcrRef></Ntry>
            </Rpt></BkToCstmrAcctRpt></Document>
            """;
        var t = Assert.Single(CamtParser.Parse(Camt, bookedDocument: true));
        Assert.Equal("009500", t.BankTransactionId);
        Assert.False(t.IdIsUnique);
    }

    [Fact]
    public async Task Bookings_sharing_a_bank_reference_are_all_stored_and_known_on_the_next_sync()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        var store = new BankTransactionStore();
        var since = new DateOnly(2026, 11, 10);
        var refresh = new AccountRefresh(null, Mt940Parser.Parse(Statement, "EUR", booked: true), []);

        for (var i = 0; i < 2; i++)
        {
            await using var db = await w.T.System.CreateDbContextAsync();
            await store.ApplyAsync(db, account, refresh, since, DateTime.UtcNow, CancellationToken.None);
        }

        var rows = (await w.TransactionsAsync(account)).Where(t => t.BankReference == "006220").ToList();
        Assert.Equal(new long[] { -5694, -5508 }, rows.OrderBy(t => t.Timestamp).Select(t => t.Amount));
        Assert.All(rows, t => Assert.StartsWith("k:", t.ExternalId, StringComparison.Ordinal));
    }
}
