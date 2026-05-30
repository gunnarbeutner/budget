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
        germany.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "sec-gold", Weight = 5000 }); // no ISIN → skipped
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
    public void Flatten_keeps_only_isin_bearing_assignments_with_root_excluded_paths()
    {
        var rows = PortfolioPerformanceFile.Flatten(SampleClient(), sourceId: 7);

        // Only the ETF assignment survives: the gold security has no ISIN, and "ghost" isn't a security.
        var row = Assert.Single(rows);
        Assert.Equal(7, row.SourceId);
        Assert.Equal("IE000WORLD0", row.Isin);
        Assert.Equal("Regions", row.TaxonomyName);
        Assert.Equal("Germany", row.ClassificationName);
        Assert.Equal("Europe / Germany", row.ClassificationPath); // root "Regions" dropped
        Assert.Equal(6000, row.WeightBp);
    }

    [Fact]
    public void ExtractSecurities_keeps_only_isin_bearing_securities()
    {
        var securities = PortfolioPerformanceFile.ExtractSecurities(SampleClient(), sourceId: 7);

        // The gold security has no ISIN and is dropped; only the ETF is catalogued.
        var sec = Assert.Single(securities);
        Assert.Equal(7, sec.SourceId);
        Assert.Equal("IE000WORLD0", sec.Isin);
        Assert.Equal("World ETF", sec.Name);
    }

    [Fact]
    public void ExtractSecurities_falls_back_to_isin_when_the_name_is_missing()
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity { Uuid = "u", Isin = "IE00NONAME0" }); // no Name

        var sec = Assert.Single(PortfolioPerformanceFile.ExtractSecurities(client, sourceId: 1));
        Assert.Equal("IE00NONAME0", sec.Name);
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
