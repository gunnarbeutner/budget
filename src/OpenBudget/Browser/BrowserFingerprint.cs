using System.Text.RegularExpressions;

namespace OpenBudget.Browser;

/// <summary>
/// A consistent Chrome-on-Linux identity: UA string and client hints without the "HeadlessChrome" giveaway.
/// Bank sign-in pages with bot detection otherwise show a captcha before the user can even type.
/// </summary>
public sealed record BrowserFingerprint(string UserAgent, string FullVersion, string Major)
{
    /// <summary>Builds the identity from the browser's product string (e.g. "HeadlessChrome/154.0.7444.59") or UA.</summary>
    public static BrowserFingerprint Create(string? productOrUserAgent)
    {
        var m = Regex.Match(productOrUserAgent ?? "", @"Chrome/(?<major>\d+)\.(?<rest>\d+\.\d+\.\d+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        var major = m.Success ? m.Groups["major"].Value : "140";
        var full = m.Success ? $"{major}.{m.Groups["rest"].Value}" : $"{major}.0.0.0";
        // Chrome's reduced UA: real major, minor parts zeroed, platform token fixed.
        var ua = $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
        return new BrowserFingerprint(ua, full, major);
    }

    /// <summary>CDP parameters of Emulation.setUserAgentOverride (with client hints).</summary>
    public object ToOverrideParams(BrowserOptions o) => new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["userAgent"] = UserAgent,
        ["acceptLanguage"] = o.AcceptLanguage,
        ["platform"] = "Linux x86_64",
        ["userAgentMetadata"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["brands"] = Brands(Major, "8"),
            ["fullVersionList"] = Brands(FullVersion, "8.0.0.0"),
            ["fullVersion"] = FullVersion,
            ["platform"] = "Linux",
            ["platformVersion"] = "6.8.0",
            ["architecture"] = "x86",
            ["model"] = "",
            ["mobile"] = false,
            ["bitness"] = "64",
            ["wow64"] = false,
        },
    };

    private static List<Dictionary<string, string>> Brands(string version, string greaseVersion) =>
    [
        new(StringComparer.Ordinal) { ["brand"] = "Not/A)Brand", ["version"] = greaseVersion },
        new(StringComparer.Ordinal) { ["brand"] = "Chromium", ["version"] = version },
        new(StringComparer.Ordinal) { ["brand"] = "Google Chrome", ["version"] = version },
    ];
}
