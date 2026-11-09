namespace OpenBudget.Browser;

/// <summary>Tuning knobs of the embedded browser used for web logins. All optional.</summary>
public sealed class BrowserOptions
{
    /// <summary>Chromium executable. Null: CHROME_PATH, then the usual install locations.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>How many web logins may be open at once (each is one browser context with one page).</summary>
    public int MaxConcurrentSessions { get; set; } = 3;

    /// <summary>Window/viewport size of the remote page; the reported screen is <see cref="ScreenWidth"/> x <see cref="ScreenHeight"/>.</summary>
    public int ViewportWidth { get; set; } = 1280;

    public int ViewportHeight { get; set; } = 800;

    public int ScreenWidth { get; set; } = 1280;

    public int ScreenHeight { get; set; } = 900;

    /// <summary>Browser language (launch --lang and navigator.language).</summary>
    public string Locale { get; set; } = "de-DE";

    /// <summary>Accept-Language header.</summary>
    public string AcceptLanguage { get; set; } = "de-DE,de;q=0.9,en;q=0.5";

    /// <summary>Null = auto (on inside a container or when running as root).</summary>
    public bool? NoSandbox { get; set; }

    public int NavigationTimeoutMs { get; set; } = 30000;

    /// <summary>JPEG quality of screencast frames while the page moves.</summary>
    public int ScreencastQuality { get; set; } = ScreencastPolicy.MovingQuality;

    /// <summary>JPEG quality of the sharp still sent shortly after the last change.</summary>
    public int StillQuality { get; set; } = ScreencastPolicy.StillQuality;

    /// <summary>The Chromium process is closed this long after the last web login ended.</summary>
    public TimeSpan IdleShutdown { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>A browser operation failed in a way worth telling the user (no Chromium, page did not open, ...).</summary>
public sealed class BrowserActionException : Exception
{
    public BrowserActionException()
    {
    }

    public BrowserActionException(string message) : base(message)
    {
    }

    public BrowserActionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
