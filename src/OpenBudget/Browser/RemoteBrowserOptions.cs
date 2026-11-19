namespace OpenBudget.Browser;

/// <summary>
/// The browser sidecar (docker/browser): a launcher that starts one headed Chrome per web login on a shared X display,
/// and selkies, which streams that display. Configured from the "Browser:Remote" section
/// (Browser__Remote__LauncherUrl, Browser__Remote__StreamUrl, Browser__Remote__Token). Unset, web logins launch Chromium
/// in-process and show the page as a screencast.
/// </summary>
public sealed class RemoteBrowserOptions
{
    public const string Section = "Browser:Remote";

    /// <summary>The path the stream is served under, by selkies (SELKIES_SUBFOLDER) and by the app's proxy alike.</summary>
    public const string StreamPath = "/weblogin/desktop/";

    /// <summary>The shortest token accepted; a shorter one leaves remote mode off.</summary>
    public const int MinTokenLength = 32;

    /// <summary>The sidecar's launcher, e.g. http://browser:9300.</summary>
    public Uri? LauncherUrl { get; set; }

    /// <summary>The sidecar's selkies server, e.g. http://browser:8080.</summary>
    public Uri? StreamUrl { get; set; }

    /// <summary>
    /// selkies' master token (SELKIES_MASTER_TOKEN); it also authorises requests to the launcher. A secret: set it from
    /// the environment, never in a committed file.
    /// </summary>
    public string? Token { get; set; }

    public bool IsEnabled =>
        LauncherUrl is { IsAbsoluteUri: true } && StreamUrl is { IsAbsoluteUri: true } && Token is { Length: >= MinTokenLength };
}
