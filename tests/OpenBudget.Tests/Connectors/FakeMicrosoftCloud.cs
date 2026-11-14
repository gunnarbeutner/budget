using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// A fake Microsoft identity platform and Microsoft Graph behind one <see cref="HttpMessageHandler"/>: the token
/// endpoint (authorization code with PKCE, refresh with rotating refresh tokens) and the OneDrive endpoints the app
/// uses (root children, shared with me, folder children, item metadata, content). Holds no real data. Shared by the
/// unit and E2E tests; never disposed by the HTTP client factory.
/// </summary>
internal sealed class FakeMicrosoftCloud : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string ClientSecret = "test-client-secret";
    public const string MyDrive = "drive-me";
    public const string SharedDrive = "drive-shared";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Challenge, string RedirectUri, string Scope)> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Expires, string Scope)> _accessTokens = new(StringComparer.Ordinal);
    private readonly List<FakeMail> _mails = [];
    private readonly Dictionary<(string Drive, string Id), FakeItem> _items = [];
    private int _counter;

    public sealed class FakeItem
    {
        public required string Drive { get; init; }
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public bool IsFolder { get; init; }
        public byte[] Content { get; set; } = [];
        public int Version { get; set; } = 1;
        public string? SharedBy { get; init; }
        public bool SharedWithMe { get; init; }
    }

    /// <summary>A mail in the fake mailbox with its attachments.</summary>
    public sealed record FakeMail(string Id, string Subject, DateTime Received, IReadOnlyList<(string Id, string Name, byte[] Content)> Attachments);

    public FakeMicrosoftCloud() => Reset();

    /// <summary>When true the consent screen leaves out Mail.Read (the app registration lacks the permission).</summary>
    public bool WithholdMail { get; set; }

    public void AddMail(FakeMail mail)
    {
        lock (_lock) _mails.Add(mail);
    }

    /// <summary>Attachment downloads so far.</summary>
    public int AttachmentDownloads => Requests.Count(r => r.EndsWith("/$value", StringComparison.Ordinal));

    /// <summary>The account name the ID token carries.</summary>
    public string AccountName { get; set; } = "someone@example.org";

    /// <summary>When true the token endpoint rejects every refresh token (consent revoked).</summary>
    public bool RejectRefresh { get; set; }


    /// <summary>Requests seen (method, path and query — never headers or bodies).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every token and code issued (for checking that none reaches a log).</summary>
    public ConcurrentQueue<string> IssuedSecrets { get; } = new();

    public int Downloads => Requests.Count(r => r.EndsWith("/content", StringComparison.Ordinal));

    public int TokenRefreshes { get; private set; }

    public void Reset()
    {
        lock (_lock)
        {
            _codes.Clear();
            _refreshTokens.Clear();
            _accessTokens.Clear();
            _items.Clear();
            _mails.Clear();
            WithholdMail = false;
            RejectRefresh = false;
            TokenRefreshes = 0;
            Requests.Clear();
            IssuedSecrets.Clear();
            AccountName = "someone@example.org";
            Add(new FakeItem { Drive = MyDrive, Id = "root", Name = "root", IsFolder = true });
        }
    }

    public FakeItem Add(FakeItem item)
    {
        lock (_lock) _items[(item.Drive, item.Id)] = item;
        return item;
    }

    /// <summary>Replaces a file's content (a new version: eTag and cTag change).</summary>
    public void Update(string drive, string id, byte[] content)
    {
        lock (_lock)
        {
            var item = _items[(drive, id)];
            item.Content = content;
            item.Version++;
        }
    }

    /// <summary>Invalidates every access token issued so far (Graph answers 401 until the app refreshes).</summary>
    public void RevokeAccessTokens()
    {
        lock (_lock) _accessTokens.Clear();
    }

    public void Remove(string drive, string id)
    {
        lock (_lock) _items.Remove((drive, id));
    }

    /// <summary>
    /// What the Microsoft sign-in page does after the user consents: checks the authorize URL and issues a code bound
    /// to its PKCE challenge and redirect URI. Returns the code and the state to send back.
    /// </summary>
    public (string Code, string State, string RedirectUri) Authorize(Uri authorizeUrl)
    {
        var q = QueryHelpers.ParseQuery(authorizeUrl.Query);
        if (q["client_id"] != ClientId) throw new InvalidOperationException("wrong client id");
        if (q["response_type"] != "code" || q["code_challenge_method"] != "S256") throw new InvalidOperationException("not a PKCE code flow");
        if (!q["scope"].ToString().Contains("offline_access", StringComparison.Ordinal)) throw new InvalidOperationException("no offline_access");
        var code = Next("code");
        var scope = Granted(q["scope"].ToString());
        lock (_lock) _codes[code] = (q["code_challenge"]!, q["redirect_uri"]!, scope);
        return (code, q["state"]!, q["redirect_uri"]!);
    }

    private string Next(string prefix)
    {
        var value = $"{prefix}-{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}";
        IssuedSecrets.Enqueue(value);
        return value;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue($"{request.Method} {uri.Host}{uri.PathAndQuery}");
        if (uri.Host == "login.microsoftonline.com")
            return Token(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (uri.Host == "graph.microsoft.com")
            return Graph(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Token(string body)
    {
        var form = QueryHelpers.ParseQuery("?" + body);
        if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret) return Error("invalid_client");
        lock (_lock)
        {
            switch (form["grant_type"].ToString())
            {
                case "authorization_code":
                {
                    var code = form["code"].ToString();
                    if (!_codes.Remove(code, out var issued)) return Error("invalid_grant");
                    var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
                    if (challenge != issued.Challenge) return Error("invalid_grant");
                    if (form["redirect_uri"] != issued.RedirectUri) return Error("invalid_grant");
                    return Issue(issued.Scope);
                }
                case "refresh_token":
                {
                    TokenRefreshes++;
                    if (RejectRefresh || !_refreshTokens.Remove(form["refresh_token"].ToString(), out var consented)) return Error("invalid_grant");
                    // Asking for a permission the user never consented to needs a new sign-in.
                    var asked = Granted(form["scope"].ToString());
                    if (asked.Split(' ').Any(a => !consented.Split(' ').Contains(a, StringComparer.Ordinal))) return Error("consent_required");
                    return Issue(asked);
                }
                default:
                    return Error("unsupported_grant_type");
            }
        }
    }

    /// <summary>The Graph permissions of a scope list that the user grants (Mail.Read only unless withheld).</summary>
    private string Granted(string scope) => string.Join(' ', scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(s => s.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal))
        .Where(s => !(WithholdMail && s.EndsWith("Mail.Read", StringComparison.Ordinal))));

    private HttpResponseMessage Issue(string scope)
    {
        var access = Next("access");
        var refresh = Next("refresh");
        _accessTokens[access] = (DateTime.UtcNow.AddHours(1), scope);
        _refreshTokens[refresh] = scope;
        var idPayload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { ["preferred_username"] = AccountName }));
        return Json(HttpStatusCode.OK, new
        {
            token_type = "Bearer",
            access_token = access,
            refresh_token = refresh,
            expires_in = 3600,
            scope,
            id_token = $"e30.{idPayload}.sig",
        });
    }

    private HttpResponseMessage Graph(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        lock (_lock)
        {
            if (token == null || !_accessTokens.TryGetValue(token, out var granted))
                return Json(HttpStatusCode.Unauthorized, new { error = new { code = "InvalidAuthenticationToken" } });

            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/v1.0/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var parts = path["/v1.0/".Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();

            if (parts is ["me", "messages", ..])
            {
                if (!granted.Scope.Contains("Mail.Read", StringComparison.Ordinal))
                    return Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorAccessDenied" } });
                return Mail(parts, request.RequestUri!);
            }
            if (parts is ["me", "drive", "root", "children"]) return Children(MyDrive, "root");
            if (parts is ["me", "drive", "sharedWithMe"])
                return Json(HttpStatusCode.OK, new { value = _items.Values.Where(i => i.SharedWithMe).Select(Remote).ToList() });
            if (parts is ["drives", var d, "items", var i, "children"]) return Children(d, i);
            if (parts is ["drives", var d2, "items", var i2])
                return _items.TryGetValue((d2, i2), out var item) ? Json(HttpStatusCode.OK, Item(item)) : NotFound();
            if (parts is ["drives", var d3, "items", var i3, "content"])
            {
                if (!_items.TryGetValue((d3, i3), out var file) || file.IsFolder) return NotFound();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) };
            }
            return NotFound();
        }
    }

    /// <summary>The mailbox: messages filtered like Graph's $filter (received since, with attachments, subject prefix), their attachments and raw content.</summary>
    private HttpResponseMessage Mail(string[] parts, Uri uri)
    {
        if (parts is ["me", "messages"])
        {
            var filter = QueryHelpers.ParseQuery(uri.Query)["$filter"].ToString();
            var since = System.Text.RegularExpressions.Regex.Match(filter, @"receivedDateTime ge (?<v>\S+)", System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            var prefixes = System.Text.RegularExpressions.Regex.Matches(filter, @"startswith\(subject,'(?<v>[^']*)'\)", System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1))
                .Select(p => p.Groups["v"].Value).ToList();
            var from = since.Success ? DateTime.Parse(since.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal) : DateTime.MinValue;
            var hits = _mails.Where(m => m.Received >= from && m.Attachments.Count > 0
                && (prefixes.Count == 0 || prefixes.Any(p => m.Subject.StartsWith(p, StringComparison.Ordinal)))).ToList();
            return Json(HttpStatusCode.OK, new { value = hits.Select(m => new { id = m.Id, subject = m.Subject, receivedDateTime = m.Received.ToString("o", System.Globalization.CultureInfo.InvariantCulture) }).ToList() });
        }
        var mail = _mails.Find(m => m.Id == parts[2]);
        if (mail == null) return NotFound();
        if (parts is [_, _, _, "attachments"])
            return Json(HttpStatusCode.OK, new { value = mail.Attachments.Select(a => new { id = a.Id, name = a.Name, size = a.Content.Length }).ToList() });
        if (parts is [_, _, _, "attachments", var attachmentId, "$value"] && mail.Attachments.FirstOrDefault(a => a.Id == attachmentId) is { Content: { } content })
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        return NotFound();
    }

    private HttpResponseMessage Children(string drive, string parent)
    {
        if (!_items.ContainsKey((drive, parent))) return NotFound();
        var children = _items.Values.Where(i => i.Drive == drive && i.ParentId == parent).ToList();
        return Json(HttpStatusCode.OK, new { value = children.Select(Item).ToList() });
    }

    private static object Item(FakeItem i) => i.IsFolder
        ? new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["eTag"] = $"\"{{{i.Id}}},{i.Version}\"",
            ["lastModifiedDateTime"] = "2026-11-01T10:00:00Z",
            ["folder"] = new { childCount = 1 },
            ["parentReference"] = new { driveId = i.Drive },
        }
        : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["size"] = i.Content.Length,
            ["eTag"] = $"\"{{{i.Id}}},{i.Version}\"",
            ["cTag"] = $"\"c:{{{i.Id}}},{i.Version}\"",
            ["lastModifiedDateTime"] = $"2026-11-0{Math.Min(i.Version, 9)}T10:00:00Z",
            ["file"] = new { mimeType = "application/octet-stream" },
            ["parentReference"] = new { driveId = i.Drive },
        };

    private static object Remote(FakeItem i)
    {
        var remote = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["parentReference"] = new { driveId = i.Drive },
            ["shared"] = new { owner = new { user = new { displayName = i.SharedBy } } },
        };
        if (i.IsFolder) remote["folder"] = new { childCount = 1 };
        else remote["file"] = new { mimeType = "application/octet-stream" };
        return new { id = "local-" + i.Id, name = i.Name, remoteItem = remote };
    }

    private static HttpResponseMessage NotFound() => Json(HttpStatusCode.NotFound, new { error = new { code = "itemNotFound" } });

    private static HttpResponseMessage Error(string error) =>
        Json(HttpStatusCode.BadRequest, new { error, error_description = $"AADSTS00000: {error}" });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
