namespace OpenBudget.E2E.Tests;

/// <summary>Finds a Chromium for the tests that drive the app's embedded browser: CHROME_PATH, else the one Playwright installed.</summary>
internal static class ChromiumLocator
{
    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(home, "Library", "Caches", "ms-playwright"), Path.Combine(home, ".cache", "ms-playwright") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root, "chromium-*").OrderByDescending(d => d, StringComparer.Ordinal))
            {
                foreach (var rel in new[]
                {
                    "chrome-mac-arm64/Chromium.app/Contents/MacOS/Chromium", "chrome-mac/Chromium.app/Contents/MacOS/Chromium", "chrome-linux/chrome",
                })
                {
                    var exe = Path.Combine(dir, rel);
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        return null;
    }
}
