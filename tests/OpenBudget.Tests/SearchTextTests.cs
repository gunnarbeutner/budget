using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("Nürnberg", "nuernberg")]
    [InlineData("Nuernberg", "nuernberg")]
    [InlineData("NÜRNBERG", "nuernberg")]
    [InlineData("Straße", "strasse")]
    [InlineData("STRASSE", "strasse")]
    [InlineData("Müller", "mueller")]
    [InlineData("Öl", "oel")]
    [InlineData("Ärger", "aerger")]
    [InlineData("Café", "cafe")]
    [InlineData("Israel", "israel")]
    [InlineData(null, "")]
    public void Fold_spells_out_umlauts_and_lowercases(string? text, string expected) =>
        Assert.Equal(expected, SearchText.Fold(text));

    [Fact]
    public void Fold_handles_decomposed_umlauts() =>
        Assert.Equal("nuernberg", SearchText.Fold("Nürnberg"));

    [Theory]
    [InlineData("VAG Nuernberg Logpay", "nürnberg")]
    [InlineData("VAG Nürnberg", "NUERNBERG")]
    [InlineData("Hauptstraße 5", "strasse")]
    [InlineData("Hauptstrasse 5", "straße")]
    [InlineData("Müller Drogerie", "mueller")]
    [InlineData("Mueller Drogerie", "Müller")]
    [InlineData("Israel", "ISRAEL")]
    [InlineData("anything", "  ")]
    public void Matches_both_directions(string haystack, string query) => Assert.True(SearchText.Matches(haystack, query));

    [Theory]
    [InlineData("Muller", "müller")]
    [InlineData("Bäckerei", "backerei")]
    public void Does_not_match_beyond_the_spellings(string haystack, string query) => Assert.False(SearchText.Matches(haystack, query));
}
