namespace OpenBudget.Connectors.OneDrive;

/// <summary>
/// The Microsoft Entra ID app registration OpenBudget signs users in with to read their OneDrive files. Comes from
/// the environment (never committed); without a client id and secret the OneDrive feature is hidden.
/// </summary>
public sealed class OneDriveOptions
{
    public const string ClientIdVariable = "OPENBUDGET_ONEDRIVE_CLIENT_ID";
    public const string ClientSecretVariable = "OPENBUDGET_ONEDRIVE_CLIENT_SECRET";
    public const string TenantVariable = "OPENBUDGET_ONEDRIVE_TENANT";

    /// <summary>Name of the <see cref="IHttpClientFactory"/> client for the identity platform and Microsoft Graph.</summary>
    public const string HttpClientName = "onedrive";

    /// <summary>The callback path; the redirect URI registered in Entra ID is the app's public base URL plus this.</summary>
    public const string CallbackPath = "/connect/onedrive/callback";

    public const string ConnectPath = "/connect/onedrive";

    /// <summary>
    /// Delegated permissions: reading the user's files and the files shared with them, a refresh token, and the
    /// account name for display.
    /// </summary>
    public const string Scopes = "openid profile offline_access https://graph.microsoft.com/Files.Read.All";

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    /// <summary>The tenant segment of the authority: a tenant id or domain, or "organizations" (any work/school account).</summary>
    public string Tenant { get; init; } = "organizations";

    public string AuthorityBase { get; init; } = "https://login.microsoftonline.com";

    public string GraphBase { get; init; } = "https://graph.microsoft.com/v1.0";

    /// <summary>Minimum time between two automatic checks of a user's linked files (page loads in between do nothing).</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a started sign-in may take before its state is no longer accepted.</summary>
    public TimeSpan StateLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Largest file downloaded (PortfolioPerformance files are a few MB).</summary>
    public long MaxDownloadBytes { get; init; } = 256L * 1024 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string AuthorizeEndpoint => $"{AuthorityBase.TrimEnd('/')}/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0/authorize";

    public string TokenEndpoint => $"{AuthorityBase.TrimEnd('/')}/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0/token";

    public static OneDriveOptions FromConfiguration(IConfiguration configuration) => new()
    {
        ClientId = configuration[ClientIdVariable]?.Trim(),
        ClientSecret = configuration[ClientSecretVariable]?.Trim(),
        Tenant = configuration[TenantVariable] is { Length: > 0 } tenant ? tenant.Trim() : "organizations",
    };
}
