using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.OneDrive;

/// <summary>The user's Microsoft connection as the settings page shows it (never carries tokens).</summary>
public sealed record CloudConnectionView(string? AccountName, DateTime ConnectedAt, bool NeedsReconnect, string? LastError);

public sealed record CloudFileLinkView(
    int Id, string DisplayPath, DateTime? LastCheckedAt, DateTime? LastImportedAt,
    DateTime? RemoteModifiedAt, string? LastError);

/// <summary>A folder to list in the picker: the user's drive root, "Shared with me", or a folder in some drive.</summary>
public sealed record CloudFolder(string? DriveId, string? ItemId, bool SharedWithMe = false)
{
    public static readonly CloudFolder MyFiles = new(null, null);
    public static readonly CloudFolder Shared = new(null, null, true);
}

/// <summary>An entry of the picker: a folder (to open) or a PortfolioPerformance file (to link).</summary>
public sealed record CloudItem(string DriveId, string ItemId, string Name, bool IsFolder, long? Size, DateTime? LastModifiedUtc, string? SharedBy);

public enum ConnectOutcome
{
    Connected,
    Denied,
    InvalidState,
    Failed,
}

/// <summary>
/// OneDrive / OneDrive for Business access for each user: connecting their Microsoft account (authorization code
/// flow with PKCE and state), keeping the encrypted refresh token current (rotated tokens are saved back), browsing
/// for ".portfolio" files, linking them, and reading them through Microsoft Graph (<see cref="ICloudFileReader"/>).
/// Every entry point takes the acting user id and only reads or writes that user's rows; access tokens live in
/// memory only and no token is ever logged.
/// </summary>
public sealed class OneDriveService : ICloudFileReader
{
    public const string PortfolioExtension = ".portfolio";

    /// <summary>
    /// Copies Portfolio Performance writes next to a file ("X.autosave.portfolio", "X.backup.portfolio",
    /// "X.backup-after-open.portfolio" and other "X.backup-….portfolio" variants); the picker leaves them out.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PortfolioCopy = new(
        @"\.(autosave|backup(-[a-z0-9-]+)?)\.portfolio$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>Whether <paramref name="name"/> is a Portfolio Performance file worth linking (not one of its automatic copies).</summary>
    public static bool IsPortfolioFile(string name) =>
        name.EndsWith(PortfolioExtension, StringComparison.OrdinalIgnoreCase) && !PortfolioCopy.IsMatch(name);
    private const int MaxListPages = 20;

    private readonly SystemDbContextFactory _system;
    private readonly MicrosoftIdentityClient _identity;
    private readonly IHttpClientFactory _http;
    private readonly SecretsProtector _secrets;
    private readonly OneDriveOptions _options;
    private readonly TimeProvider _clock;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<OneDriveService> _logger;
    private readonly ConcurrentDictionary<string, PendingSignIn> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MicrosoftTokens> _accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    private sealed record PendingSignIn(string UserId, string CodeVerifier, Uri RedirectUri, DateTime ExpiresAtUtc);

    public OneDriveService(
        SystemDbContextFactory system, MicrosoftIdentityClient identity, IHttpClientFactory http, SecretsProtector secrets,
        OneDriveOptions options, TimeProvider clock, ChangeNotificationService notifier, ILogger<OneDriveService> logger)
    {
        _system = system;
        _identity = identity;
        _http = http;
        _secrets = secrets;
        _options = options;
        _clock = clock;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Whether the app registration and the secrets key are configured; otherwise the feature is hidden.</summary>
    public bool IsEnabled => _options.IsConfigured && _secrets.IsConfigured;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static string Purpose(string userId) => $"onedrive-refresh:{userId}";

    // ---- Connecting ----

    /// <summary>Starts a sign-in for <paramref name="userId"/>: remembers state and PKCE verifier, returns the Microsoft URL to send the browser to.</summary>
    public Uri BeginConnect(string userId, Uri redirectUri)
    {
        RequireEnabled();
        var now = Now;
        foreach (var (key, stale) in _pending)
            if (stale.ExpiresAtUtc <= now) _pending.TryRemove(key, out _);

        var state = MicrosoftIdentityClient.NewState();
        var verifier = MicrosoftIdentityClient.NewCodeVerifier();
        _pending[state] = new PendingSignIn(userId, verifier, redirectUri, now + _options.StateLifetime);
        return _identity.BuildAuthorizeUrl(state, MicrosoftIdentityClient.CodeChallenge(verifier), redirectUri);
    }

    /// <summary>
    /// Completes a sign-in from the callback. The state must be one this server issued to the same user within
    /// its lifetime (it is single-use); then the code is redeemed with the matching PKCE verifier and the refresh
    /// token stored (replacing an earlier connection of the user, whose linked files are kept).
    /// </summary>
    public async Task<ConnectOutcome> CompleteConnectAsync(string userId, string? state, string? code, string? error, CancellationToken ct)
    {
        RequireEnabled();
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending)
            || pending.UserId != userId || pending.ExpiresAtUtc <= Now)
            return ConnectOutcome.InvalidState;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return ConnectOutcome.Denied;

        MicrosoftTokens tokens;
        try
        {
            tokens = await _identity.RedeemCodeAsync(code, pending.CodeVerifier, pending.RedirectUri, ct);
        }
        catch (CloudException ex)
        {
            _logger.LogWarning(ex, "Connecting OneDrive for user {UserId} failed.", userId);
            return ConnectOutcome.Failed;
        }
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            _logger.LogWarning("Connecting OneDrive for user {UserId} failed: no refresh token was issued.", userId);
            return ConnectOutcome.Failed;
        }

        await using var db = await _system.CreateDbContextAsync(ct);
        var connection = await db.CloudConnections.FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct);
        if (connection == null)
        {
            connection = new CloudConnection { OwnerUserId = userId, Provider = CloudProviders.OneDrive };
            db.CloudConnections.Add(connection);
        }
        connection.AccountName = Truncate(tokens.AccountName, 300);
        connection.EncryptedRefreshToken = _secrets.Encrypt(tokens.RefreshToken, Purpose(userId));
        connection.ConnectedAt = Now;
        connection.NeedsReconnect = false;
        connection.LastError = null;
        await db.SaveChangesAsync(ct);
        _accessTokens[userId] = tokens;
        Changed(userId);
        return ConnectOutcome.Connected;
    }

    public async Task<CloudConnectionView?> GetConnectionAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var c = await db.CloudConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct);
        return c == null ? null : new CloudConnectionView(c.AccountName, c.ConnectedAt, c.NeedsReconnect, c.LastError);
    }

    /// <summary>Forgets the user's connection, its refresh token and all their linked files.</summary>
    public async Task DisconnectAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var connection = await db.CloudConnections.Include(c => c.Files)
            .FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct);
        _accessTokens.TryRemove(userId, out _);
        if (connection == null) return;
        db.CloudConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    // ---- Picker ----

    /// <summary>The folders and PortfolioPerformance files in <paramref name="folder"/>, folders first.</summary>
    public async Task<IReadOnlyList<CloudItem>> ListAsync(string userId, CloudFolder folder, CancellationToken ct = default)
    {
        RequireEnabled();
        var url = folder switch
        {
            { SharedWithMe: true, ItemId: null } => $"{_options.GraphBase}/me/drive/sharedWithMe",
            { DriveId: { } d, ItemId: { } i } => $"{_options.GraphBase}/drives/{Uri.EscapeDataString(d)}/items/{Uri.EscapeDataString(i)}/children",
            _ => $"{_options.GraphBase}/me/drive/root/children",
        };

        var items = new List<CloudItem>();
        for (var page = 0; url != null && page < MaxListPages; page++)
        {
            var pageUrl = url;
            using var response = await GraphAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, pageUrl), ct);
            await EnsureSuccessAsync(response, userId, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var values))
                foreach (var element in values.EnumerateArray())
                    if (ToItem(element) is { } item && (item.IsFolder || IsPortfolioFile(item.Name)))
                        items.Add(item);
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return items.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static CloudItem? ToItem(JsonElement e)
    {
        // Items shared with the user point at the sharer's drive through remoteItem.
        var source = e.TryGetProperty("remoteItem", out var remote) ? remote : e;
        var id = Str(source, "id");
        var driveId = source.TryGetProperty("parentReference", out var parent) ? Str(parent, "driveId") : null;
        var name = Str(e, "name") ?? Str(source, "name");
        if (id == null || driveId == null || name == null) return null;
        var isFolder = source.TryGetProperty("folder", out _) || e.TryGetProperty("folder", out _);
        var size = source.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : (long?)null;
        string? sharedBy = null;
        if (source.TryGetProperty("shared", out var shared) && shared.TryGetProperty("owner", out var owner) && owner.TryGetProperty("user", out var user))
            sharedBy = Str(user, "displayName");
        return new CloudItem(driveId, id, name, isFolder, size, Date(source, "lastModifiedDateTime") ?? Date(e, "lastModifiedDateTime"), sharedBy);
    }

    // ---- Linked files ----

    public async Task<List<CloudFileLinkView>> GetLinkedFilesAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var links = await db.CloudFileLinks.AsNoTracking().Where(f => f.OwnerUserId == userId).OrderBy(f => f.DisplayPath).ToListAsync(ct);
        return links.Select(f => new CloudFileLinkView(f.Id, f.DisplayPath, f.LastCheckedAt, f.LastImportedAt, f.RemoteModifiedAt, f.LastError)).ToList();
    }

    /// <summary>
    /// Links a file of the user's drive (or one shared with them) for server-side reading. The target source, when
    /// given, must be one the user owns. Linking the same file again updates its path and target.
    /// </summary>
    public async Task<int> LinkFileAsync(string userId, string driveId, string itemId, string displayPath, CancellationToken ct = default)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("A file is required.", nameof(itemId));
        await using var db = await _system.CreateDbContextAsync(ct);
        var connection = await db.CloudConnections.FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct)
            ?? throw new CloudException("Connect to Microsoft first.");

        // The file must exist and be readable with the user's access.
        var metadata = await GetItemMetadataAsync(userId, driveId, itemId, ct);

        var link = await db.CloudFileLinks.FirstOrDefaultAsync(f => f.ConnectionId == connection.Id && f.DriveId == driveId && f.ItemId == itemId, ct);
        if (link == null)
        {
            link = new CloudFileLink { ConnectionId = connection.Id, OwnerUserId = userId, DriveId = driveId, ItemId = itemId, CreatedAt = Now };
            db.CloudFileLinks.Add(link);
        }
        link.DisplayPath = Truncate(string.IsNullOrWhiteSpace(displayPath) ? metadata.Name : displayPath.Trim(), 1000)!;
        link.LastCTag = null;
        link.LastETag = null;
        link.LastError = null;
        link.LastCheckedAt = null;
        await db.SaveChangesAsync(ct);
        Changed(userId);
        return link.Id;
    }

    public async Task<bool> UnlinkFileAsync(string userId, int linkId, CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        var link = await db.CloudFileLinks.FirstOrDefaultAsync(f => f.Id == linkId && f.OwnerUserId == userId, ct);
        if (link == null) return false;
        db.CloudFileLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        Changed(userId);
        return true;
    }

    // ---- ICloudFileReader ----

    public async Task<CloudFileMetadata> GetMetadataAsync(string userId, int linkId, CancellationToken ct)
    {
        var link = await FindLinkAsync(userId, linkId, ct);
        return await GetItemMetadataAsync(userId, link.DriveId, link.ItemId, ct);
    }

    public async Task<CloudFileContent> ReadAsync(string userId, int linkId, string? knownContentTag, CancellationToken ct)
    {
        var link = await FindLinkAsync(userId, linkId, ct);
        var metadata = await GetItemMetadataAsync(userId, link.DriveId, link.ItemId, ct);
        if (knownContentTag != null && metadata.ContentTag == knownContentTag)
            return new CloudFileContent(metadata, null);
        if (metadata.Size > _options.MaxDownloadBytes)
            throw new CloudException("The file is too large to read.");

        var url = $"{_options.GraphBase}/drives/{Uri.EscapeDataString(link.DriveId)}/items/{Uri.EscapeDataString(link.ItemId)}/content";
        using var response = await GraphAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        await EnsureSuccessAsync(response, userId, ct);
        if (response.Content.Headers.ContentLength > _options.MaxDownloadBytes)
            throw new CloudException("The file is too large to read.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxDownloadBytes) throw new CloudException("The file is too large to read.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return new CloudFileContent(metadata, buffer.ToArray());
    }

    private async Task<CloudFileLink> FindLinkAsync(string userId, int linkId, CancellationToken ct)
    {
        RequireEnabled();
        await using var db = await _system.CreateDbContextAsync(ct);
        return await db.CloudFileLinks.AsNoTracking().FirstOrDefaultAsync(f => f.Id == linkId && f.OwnerUserId == userId, ct)
            ?? throw new KeyNotFoundException("Unknown linked file.");
    }

    private async Task<CloudFileMetadata> GetItemMetadataAsync(string userId, string driveId, string itemId, CancellationToken ct)
    {
        var url = $"{_options.GraphBase}/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}"
            + "?$select=id,name,eTag,cTag,lastModifiedDateTime,size,file,folder";
        using var response = await GraphAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        await EnsureSuccessAsync(response, userId, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("folder", out _)) throw new CloudException("The linked item is a folder, not a file.");
        return new CloudFileMetadata(
            Str(root, "name") ?? "",
            Str(root, "eTag"),
            Str(root, "cTag"),
            Date(root, "lastModifiedDateTime"),
            root.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : null);
    }

    // ---- Graph and tokens ----

    /// <summary>Sends a Graph request with the user's access token; a 401 refreshes the token once and retries.</summary>
    private async Task<HttpResponseMessage> GraphAsync(string userId, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = _http.CreateClient(OneDriveOptions.HttpClientName);
        var retried = false;
        while (true)
        {
            var token = await GetAccessTokenAsync(userId, forceRefresh: retried, ct);
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new CloudException("Microsoft OneDrive could not be reached.", ex);
            }
            if (response.StatusCode != HttpStatusCode.Unauthorized || retried) return response;
            response.Dispose();
            retried = true;
            _accessTokens.TryRemove(userId, out _);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string userId, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        if (status == HttpStatusCode.Unauthorized)
        {
            const string message = "Microsoft no longer accepts OpenBudget's access. Reconnect to Microsoft.";
            await MarkReconnectAsync(userId, message, ct);
            throw new CloudReconnectRequiredException(message);
        }
        throw new CloudException((int)status switch
        {
            404 => "The file or folder no longer exists or is no longer shared with you.",
            403 => "Your Microsoft account has no access to this file or folder.",
            429 => "Microsoft OneDrive is throttling requests; try again later.",
            _ => $"Microsoft OneDrive answered with HTTP {(int)status}.",
        });
    }

    private async Task<string> GetAccessTokenAsync(string userId, bool forceRefresh, CancellationToken ct)
    {
        RequireEnabled();
        if (!forceRefresh && _accessTokens.TryGetValue(userId, out var cached) && cached.ExpiresAtUtc > Now.AddMinutes(2))
            return cached.AccessToken;

        var gate = _refreshLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (!forceRefresh && _accessTokens.TryGetValue(userId, out cached) && cached.ExpiresAtUtc > Now.AddMinutes(2))
                return cached.AccessToken;

            await using var db = await _system.CreateDbContextAsync(ct);
            var connection = await db.CloudConnections.FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct)
                ?? throw new CloudException("Connect to Microsoft first.");
            if (connection.NeedsReconnect)
                throw new CloudReconnectRequiredException(connection.LastError ?? "Reconnect to Microsoft.");
            if (!_secrets.TryDecrypt(connection.EncryptedRefreshToken, Purpose(userId), out var refreshToken))
            {
                await MarkReconnectAsync(userId, "The stored Microsoft sign-in cannot be read (secrets key changed). Reconnect to Microsoft.", ct);
                throw new CloudReconnectRequiredException("Reconnect to Microsoft.");
            }

            MicrosoftTokens tokens;
            try
            {
                tokens = await _identity.RefreshAsync(refreshToken, ct);
            }
            catch (CloudReconnectRequiredException ex)
            {
                await MarkReconnectAsync(userId, ex.Message, ct);
                throw;
            }

            // Microsoft rotates refresh tokens: keep the newest.
            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refreshToken)
            {
                connection.EncryptedRefreshToken = _secrets.Encrypt(tokens.RefreshToken, Purpose(userId));
                await db.SaveChangesAsync(ct);
            }
            _accessTokens[userId] = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MarkReconnectAsync(string userId, string message, CancellationToken ct)
    {
        _accessTokens.TryRemove(userId, out _);
        await using var db = await _system.CreateDbContextAsync(ct);
        var connection = await db.CloudConnections.FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct);
        if (connection == null) return;
        connection.NeedsReconnect = true;
        connection.LastError = Truncate(message, 1000);
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    private void RequireEnabled()
    {
        if (!IsEnabled) throw new InvalidOperationException("OneDrive is not configured.");
    }

    private void Changed(string userId) => _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: userId));

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.UtcDateTime
            : null;

    private static string? Truncate(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
