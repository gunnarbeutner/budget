using System.Text.Json;
using OpenBudget.Browser;
using Xunit;

namespace OpenBudget.Tests.Browser;

public sealed class BrowserFingerprintTests
{
    [Theory]
    [InlineData("HeadlessChrome/154.0.7444.59", "154", "154.0.7444.59")]
    [InlineData("Chrome/140.0.7339.80", "140", "140.0.7339.80")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.7444.59 Safari/537.36", "154", "154.0.7444.59")]
    [InlineData(null, "140", "140.0.0.0")]
    [InlineData("unknown", "140", "140.0.0.0")]
    public void Browser_version_is_preserved_in_the_user_agent_and_client_hints(string? product, string major, string full)
    {
        var fingerprint = BrowserFingerprint.Create(product);

        Assert.Equal(major, fingerprint.Major);
        Assert.Equal(full, fingerprint.FullVersion);
        Assert.Equal($"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36", fingerprint.UserAgent);

        var parameters = JsonSerializer.SerializeToElement(fingerprint.ToOverrideParams(new BrowserOptions()));
        Assert.Equal(fingerprint.UserAgent, parameters.GetProperty("userAgent").GetString());
        var metadata = parameters.GetProperty("userAgentMetadata");
        Assert.Equal(full, metadata.GetProperty("fullVersion").GetString());
        Assert.Equal(major, metadata.GetProperty("brands")[1].GetProperty("version").GetString());
        Assert.Equal(major, metadata.GetProperty("brands")[2].GetProperty("version").GetString());
        Assert.Equal(full, metadata.GetProperty("fullVersionList")[1].GetProperty("version").GetString());
        Assert.Equal(full, metadata.GetProperty("fullVersionList")[2].GetProperty("version").GetString());
    }
}
