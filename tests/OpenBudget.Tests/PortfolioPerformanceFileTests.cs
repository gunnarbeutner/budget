using System.IO.Compression;
using Google.Protobuf;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class PortfolioPerformanceFileTests
{
    // Wraps a PClient the way PortfolioPerformance does on disk: ZIP with a single
    // "data.portfolio" entry = "PPPBV1" signature + serialized PClient.
    private static MemoryStream Pack(PClient client)
    {
        var payload = client.ToByteArray();
        var signed = new byte[6 + payload.Length];
        "PPPBV1"u8.CopyTo(signed);
        payload.CopyTo(signed, 6);

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("data.portfolio");
            using var es = entry.Open();
            es.Write(signed, 0, signed.Length);
        }
        zip.Position = 0;
        return zip;
    }

    private static PClient SampleClient()
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity { Uuid = "sec-etf", Name = "World ETF", Isin = "IE000WORLD0" });
        client.Securities.Add(new PSecurity { Uuid = "sec-gold", Name = "Gold" }); // no ISIN

        var tax = new PTaxonomy { Id = "tax-regions", Name = "Regions" };
        // Flat classifications linked by parentId; root has no parent and should be dropped from paths.
        tax.Classifications.Add(new PTaxonomy.Types.Classification { Id = "root", Name = "Regions" });
        tax.Classifications.Add(new PTaxonomy.Types.Classification { Id = "eu", ParentId = "root", Name = "Europe" });
        var germany = new PTaxonomy.Types.Classification { Id = "de", ParentId = "eu", Name = "Germany" };
        germany.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "sec-etf", Weight = 6000 });
        germany.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "sec-gold", Weight = 5000 }); // no ISIN → kept under its uuid
        germany.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "ghost", Weight = 1000 }); // unknown vehicle → skipped
        tax.Classifications.Add(germany);
        client.Taxonomies.Add(tax);
        return client;
    }

    [Fact]
    public void ReadClient_round_trips_a_packed_client()
    {
        using var packed = Pack(SampleClient());
        var client = PortfolioPerformanceFile.ReadClient(packed);

        Assert.Equal(2, client.Securities.Count);
        Assert.Single(client.Taxonomies);
        Assert.Equal("Regions", client.Taxonomies[0].Name);
    }

    [Fact]
    public void Flatten_keeps_security_assignments_with_root_excluded_paths()
    {
        var rows = PortfolioPerformanceFile.Flatten(SampleClient(), ownerId: TestDb.DefaultOwner);

        // "ghost" isn't a security, so only the ETF and the gold assignments survive.
        Assert.Equal(2, rows.Count);
        var gold = rows.Single(r => r.SecurityUuid == "sec-gold");
        Assert.Equal("", gold.Isin);
        Assert.Equal("Europe / Germany", gold.ClassificationPath);
        Assert.Equal(5000, gold.WeightBp);

        var row = rows.Single(r => r.SecurityUuid == "sec-etf");
        Assert.Equal(TestDb.DefaultOwner, row.OwnerUserId);
        Assert.Equal("IE000WORLD0", row.Isin);
        Assert.Equal("Regions", row.TaxonomyName);
        Assert.Equal("Germany", row.ClassificationName);
        Assert.Equal("Europe / Germany", row.ClassificationPath); // root "Regions" dropped
        Assert.Equal(6000, row.WeightBp);
    }

    [Fact]
    public void ExtractSecurities_catalogues_every_uuid_bearing_security_isin_or_not()
    {
        var securities = PortfolioPerformanceFile.ExtractSecurities(SampleClient(), ownerId: TestDb.DefaultOwner);

        Assert.Equal(2, securities.Count);
        var etf = securities.Single(s => s.Uuid == "sec-etf");
        Assert.Equal(TestDb.DefaultOwner, etf.OwnerUserId);
        Assert.Equal("IE000WORLD0", etf.Isin);
        Assert.Equal("World ETF", etf.Name);
        // The ISIN-less gold is catalogued too, with an empty ISIN — it maps to the source app by hand.
        var gold = securities.Single(s => s.Uuid == "sec-gold");
        Assert.Equal("", gold.Isin);
        Assert.Equal("Gold", gold.Name);
    }

    [Fact]
    public void ExtractSecurities_falls_back_to_isin_then_uuid_when_the_name_is_missing()
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity { Uuid = "u1", Isin = "IE00NONAME0" });  // no Name → ISIN
        client.Securities.Add(new PSecurity { Uuid = "u2" });                        // no Name, no ISIN → uuid

        var got = PortfolioPerformanceFile.ExtractSecurities(client, ownerId: TestDb.DefaultOwner);
        Assert.Equal("IE00NONAME0", got.Single(s => s.Uuid == "u1").Name);
        Assert.Equal("u2", got.Single(s => s.Uuid == "u2").Name);
    }

    [Fact]
    public void ExtractSecurities_reads_the_latest_quote_as_the_price()
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity
        {
            Uuid = "etf",
            Isin = "IE00X",
            Name = "World ETF",
            CurrencyCode = "EUR",
            Latest = new PFullHistoricalPrice { Date = 20000, Close = 9512000000L }  // 95.12 ×1e8
        });
        client.Securities.Add(new PSecurity { Uuid = "np", Isin = "IE00Y", Name = "No price" }); // no latest quote

        var got = PortfolioPerformanceFile.ExtractSecurities(client, ownerId: TestDb.DefaultOwner);

        var etf = got.Single(s => s.Uuid == "etf");
        Assert.Equal(95.12m, etf.Price);
        Assert.Equal("EUR", etf.Currency);
        var np = got.Single(s => s.Uuid == "np");
        Assert.Null(np.Price);
        Assert.Null(np.Currency);
    }

    [Fact]
    public void ExtractPortfolios_returns_named_depots_falling_back_to_uuid()
    {
        var client = new PClient();
        client.Portfolios.Add(new PPortfolio { Uuid = "p1", Name = "Example-Depot" });
        client.Portfolios.Add(new PPortfolio { Uuid = "p2", Name = "" });

        var got = PortfolioPerformanceFile.ExtractPortfolios(client, ownerId: TestDb.DefaultOwner);

        Assert.Equal(2, got.Count);
        Assert.All(got, p => Assert.Equal(TestDb.DefaultOwner, p.OwnerUserId));
        Assert.Equal("Example-Depot", got.Single(p => p.Uuid == "p1").Name);
        Assert.Equal("p2", got.Single(p => p.Uuid == "p2").Name);   // blank name → uuid
    }

    [Fact]
    public void ReadClient_rejects_a_file_without_the_signature()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("data.portfolio");
            using var es = entry.Open();
            es.Write("<?xml version=\"1.0\"?>"u8);
        }
        zip.Position = 0;

        Assert.Throws<InvalidDataException>(() => PortfolioPerformanceFile.ReadClient(zip));
    }

    [Fact]
    public void ReadClient_rejects_an_archive_without_the_data_entry()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("something-else.txt");
        }
        zip.Position = 0;

        Assert.Throws<InvalidDataException>(() => PortfolioPerformanceFile.ReadClient(zip));
    }
}
