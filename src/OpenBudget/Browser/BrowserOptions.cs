namespace OpenBudget.Browser;

/// <summary>Tuning knobs of the browser sessions web logins run in. All optional.</summary>
public sealed class BrowserOptions
{
    /// <summary>How many web logins may be open at once (each is one Chrome with one page).</summary>
    public int MaxConcurrentSessions { get; set; } = 3;

    /// <summary>How long a web login waits for one of the <see cref="MaxConcurrentSessions"/> slots before it fails.</summary>
    public TimeSpan SlotWait { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a session waits for another browser of the same long-lived profile to close (a kept browser, say) before
    /// it runs in an ephemeral profile instead.
    /// </summary>
    public TimeSpan ProfileWait { get; set; } = TimeSpan.FromSeconds(30);

    public int NavigationTimeoutMs { get; set; } = 30000;
}

/// <summary>A browser operation failed in a way worth telling the user (no browser sidecar, page did not open, ...).</summary>
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
