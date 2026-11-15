using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenBudget.Connectors.Sync;
using OpenBudget.Shared;
using Xunit;

namespace OpenBudget.Tests.Connectors;

public class SyncProgressTextTests
{
    private static StringLocalizer<BankSyncHost> Localizer()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance);
        return new StringLocalizer<BankSyncHost>(factory);
    }

    private static T In<T>(string culture, Func<T> format)
    {
        var saved = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try { return format(); }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    [Theory]
    [InlineData("en", "Fetching documents: 3 of 10")]
    [InlineData("de", "Dokumente werden abgerufen: 3 von 10")]
    public void DocumentsWithTotal(string culture, string expected)
    {
        var l = Localizer();
        Assert.Equal(expected, In(culture, () => SyncProgressText.Format(new SyncProgress(SyncStage.FetchingDocuments, 3, 10), l)));
    }

    [Fact]
    public void DocumentsWithoutTotal()
    {
        var l = Localizer();
        Assert.Equal("Fetching documents: 4", In("en", () => SyncProgressText.Format(new SyncProgress(SyncStage.FetchingDocuments, 4), l)));
    }

    [Fact]
    public void EveryStageHasAName()
    {
        var l = Localizer();
        foreach (var stage in Enum.GetValues<SyncStage>().Where(s => s != SyncStage.Connector))
            foreach (var culture in new[] { "en", "de" })
                Assert.False(In(culture, () => l["Progress_" + stage].ResourceNotFound), $"{stage} ({culture})");
    }

    [Fact]
    public void DetailAndConnectorText()
    {
        var l = Localizer();
        Assert.Equal("Refreshing: Giro", In("en", () => SyncProgressText.Format(new SyncProgress(SyncStage.Refreshing, Detail: "Giro"), l)));
        Assert.Equal("Reading Example payments (2 pages)",
            In("de", () => SyncProgressText.Format(new SyncProgress(SyncStage.Connector, Detail: "Reading Example payments (2 pages)"), l)));
        Assert.Equal("Logging in", In("en", () => SyncProgressText.Format(new SyncProgress(SyncStage.LoggingIn), l)));
    }

    [Fact]
    public void OnlyCountsShowInTheChip()
    {
        Assert.True(SyncProgressText.ShowsInChip(new SyncProgress(SyncStage.FetchingDocuments, 1)));
        Assert.False(SyncProgressText.ShowsInChip(new SyncProgress(SyncStage.Connector, Detail: "x")));
        Assert.False(SyncProgressText.ShowsInChip(null));
    }
}
