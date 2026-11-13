namespace OpenBudget.Browser;

/// <summary>Tuning knobs of the embedded browser used for web logins. All optional.</summary>
public sealed class BrowserOptions
{
    /// <summary>Chromium executable. Null: CHROME_PATH, then the usual install locations.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>How many web logins may be open at once (each is one browser context with one page).</summary>
    public int MaxConcurrentSessions { get; set; } = 3;

    /// <summary>How long a web login waits for one of the <see cref="MaxConcurrentSessions"/> slots before it fails.</summary>
    public TimeSpan SlotWait { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Initial browser window size; frames and input use the native page's actual viewport.</summary>
    public int ViewportWidth { get; set; } = 1280;

    public int ViewportHeight { get; set; } = 800;

    /// <summary>Browser language (Chromium's --lang option).</summary>
    public string Locale { get; set; } = "de-DE";

    /// <summary>
    /// Runs Chromium without a window (its built-in headless mode) instead of the headed browser web logins use;
    /// for automated tests, so they open no windows.
    /// </summary>
    public bool Headless { get; set; }

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
