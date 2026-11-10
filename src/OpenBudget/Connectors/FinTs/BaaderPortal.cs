#pragma warning disable S1075 // the portal's address is a fixed, published URL
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// Baader Bank's customer portal (konto.baaderbank.de, "Baader Kundenportal"): the documents (Postfach) that FinTS does
/// not offer. This is Baader's own portal for its direct customers (Smartbroker+ for example), not the one of a broker
/// front-end: documents of brokers that only use Baader as custodian come from that broker's own connector.
///
/// Protocol (as observed from the portal):
/// <list type="bullet">
/// <item><c>GET /fstatus</c>: JSON array of the signed-in customers (<c>customer_id</c>, ...); without a session it redirects to <c>/login</c>.</item>
/// <item>Sign-in: a form at <c>/login</c> (fields <c>user</c>, <c>password</c>, a CSRF <c>token</c> and a script-filled <c>pin</c>,
/// sometimes followed by an SMS-TAN) that redirects to <c>/dashboard</c>. It runs in the app's browser (a web login with
/// autofill) so the page's scripts and a TAN prompt work as they do for a person.</item>
/// <item><c>POST /mailrows/{customerId}</c> per document type and period: JSON <c>[{"yyyy-MM-dd": [[read, title, id], ...]}]</c>,
/// or <c>{"do":"error","title":...}</c> when the period holds none.</item>
/// <item><c>GET /maildownload/{id}</c>: redirects to a short-lived storage URL that serves the PDF.</item>
/// </list>
/// The request body of <c>/mailrows</c> was not visible in what was observed; the field names below are the best guess and
/// live in one place (<see cref="ListFields"/>) so they are easy to correct.
/// </summary>
internal sealed partial class BaaderPortal
{
    public static readonly Uri DefaultBaseUri = new("https://konto.baaderbank.de/");
    internal const string WebLoginReference = "baader-portal-web-login";
    internal const string IdentifierPrefix = "baader:";
    internal const string UsernameSelector = "input#user, input[name=user]";
    internal const string PasswordSelector = "input#password, input[name=password]";
    internal const string SubmitSelector = "button#ctaLogin, form#loginform button[type=submit]";

    /// <summary>A period the portal lists at once (it offers 90 days by default).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>How many periods the first download walks back (about three years).</summary>
    internal const int FirstSyncWindows = 12;

    /// <summary>The portal's document types and which account their documents belong to.</summary>
    internal static readonly IReadOnlyList<(string Section, DocumentTarget Target)> Sections =
    [
        ("ACCOUNT_STATEMENT", DocumentTarget.Cash), // account statements, interest
        ("NOTIFICATIONS", DocumentTarget.Depot), // "Mitteilungen": trade confirmations, corporate actions
        ("TAXES", DocumentTarget.Login), // tax documents
        ("CREDIT", DocumentTarget.Login),
        ("MISC", DocumentTarget.Login), // cost reports, quarterly statements, ...
    ];

    internal enum DocumentTarget
    {
        Cash,
        Depot,
        Login,
    }

    private readonly IBankHttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Uri _base;
    private string? _token;

    public BaaderPortal(IBankHttpClient http, TimeProvider clock, ILogger log, Uri? baseUri = null)
    {
        _http = http;
        _clock = clock;
        _log = log;
        _base = baseUri ?? DefaultBaseUri;
    }

    internal WebLoginSpec WebLogin(ConnectorCredentials? credentials) => new(
        new Uri(_base, "login"), [_base.Host], successUrlPrefix: new Uri(_base, "dashboard"))
    {
        Autofill = credentials == null ? null
            : new WebLoginAutofill(credentials.Username, credentials.Secret, UsernameSelector, PasswordSelector, SubmitSelector),
    };

    /// <summary>
    /// New documents for the wanted accounts. Asks for a web login when the stored portal session is gone; called again
    /// after it, it continues with the browser's cookies.
    /// </summary>
    public async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> known, ConnectorCredentials? credentials, ChallengeAnswer? answer, CancellationToken ct)
    {
        var customers = await CustomersAsync(ct);
        if (customers == null)
        {
            if (answer?.Challenge.Kind == ChallengeKind.WebLogin)
                throw new ConnectorException("The sign-in on the Baader portal did not complete.");
            _log.LogInformation("No valid Baader portal session; asking for a sign-in.");
            return ConnectorStep.Ask<IReadOnlyList<RemoteStatement>>(ConnectorChallenge.WebLogin(
                "Sign in on the Baader portal to fetch documents.", WebLogin(credentials), WebLoginReference));
        }
        _token = await TokenAsync(ct);

        var cash = accounts.FirstOrDefault(a => a.Type != RemoteAccountType.Depot)?.RemoteId;
        var depot = accounts.FirstOrDefault(a => a.Type == RemoteAccountType.Depot)?.RemoteId;
        var incremental = known.Any(k => k.StartsWith(IdentifierPrefix, StringComparison.Ordinal));
        var result = new List<RemoteStatement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var customer in customers)
        {
            foreach (var (section, target) in Sections)
            {
                var account = target switch
                {
                    DocumentTarget.Cash => cash,
                    DocumentTarget.Depot => depot ?? cash,
                    DocumentTarget.Login => null,
                    _ => null,
                };
                // A cash/depot document whose account's statements are off: not wanted.
                if (target != DocumentTarget.Login && account == null) continue;
                foreach (var doc in await ListAsync(customer, section, incremental, known, ct))
                {
                    var identifier = IdentifierPrefix + doc.Id;
                    if (known.Contains(identifier) || !seen.Add(identifier)) continue;
                    var bytes = await DownloadAsync(doc.Id, ct);
                    if (bytes == null) continue;
                    result.Add(new RemoteStatement(identifier, account, doc.Date, doc.Title, bytes));
                }
            }
        }
        _log.LogInformation("Baader portal: {Count} new document(s).", result.Count);
        return ConnectorStep.Done<IReadOnlyList<RemoteStatement>>(result);
    }

    internal sealed record PortalDocument(string Id, DateOnly Date, string Title);

    /// <summary>
    /// The documents of one type, newest period first. Walks back period by period: on the first download as far as
    /// <see cref="FirstSyncWindows"/>, later only until a period brings nothing new.
    /// </summary>
    private async Task<List<PortalDocument>> ListAsync(string customer, string section, bool incremental, IReadOnlySet<string> known, CancellationToken ct)
    {
        var docs = new List<PortalDocument>();
        var to = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < FirstSyncWindows; i++)
        {
            var from = to.AddDays(-(int)Window.TotalDays); // the portal's own default period: 91 days inclusive
            var page = await ListWindowAsync(customer, section, from, to, ct);
            docs.AddRange(page);
            var anythingNew = page.Any(d => !known.Contains(IdentifierPrefix + d.Id));
            if (incremental && !anythingNew) break;
            to = from.AddDays(-1);
        }
        return docs;
    }

    /// <summary>The form fields of a list request (unverified guess, see the class remarks).</summary>
    internal static IEnumerable<KeyValuePair<string, string>> ListFields(string section, DateOnly from, DateOnly to, string? token)
    {
        yield return new("section", section);
        yield return new("from", from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        yield return new("to", to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        if (token != null) yield return new("token", token);
    }

    private async Task<List<PortalDocument>> ListWindowAsync(string customer, string section, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(ListFields(section, from, to, _token));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "mailrows/" + Uri.EscapeDataString(customer))) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.Referrer = new Uri(_base, "mail");
        using var response = await _http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct);
        return ParseRows(json, section);
    }

    /// <summary>Reads the documents of a <c>/mailrows</c> answer; "no documents in this period" is an empty list.</summary>
    internal static List<PortalDocument> ParseRows(JsonElement json, string section)
    {
        var docs = new List<PortalDocument>();
        if (json.ValueKind == JsonValueKind.Object)
        {
            if (json.TryGetProperty("do", out var verb) && string.Equals(verb.GetString(), "error", StringComparison.Ordinal))
            {
                var title = json.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (title.Contains("keine Dokumente", StringComparison.OrdinalIgnoreCase) || title.Contains("no documents", StringComparison.OrdinalIgnoreCase))
                    return docs;
                throw new ConnectorException($"The Baader portal refused the document list ({section}): {Shorten(title)}");
            }
            throw new ConnectorException($"The Baader portal answered the document list ({section}) in an unexpected form.");
        }
        if (json.ValueKind != JsonValueKind.Array)
            throw new ConnectorException($"The Baader portal answered the document list ({section}) in an unexpected form.");

        foreach (var group in json.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            foreach (var day in group.EnumerateObject())
            {
                if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || day.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in day.Value.EnumerateArray())
                {
                    // [read flag, title, document id]
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;
                    var title = row[1].ValueKind == JsonValueKind.String ? row[1].GetString() : null;
                    var id = row[2].ValueKind == JsonValueKind.String ? row[2].GetString() : null;
                    if (id == null || !DocumentId().IsMatch(id)) continue;
                    docs.Add(new PortalDocument(id, date, string.IsNullOrWhiteSpace(title) ? "Baader document" : title.Trim()));
                }
            }
        }
        return docs;
    }

    private async Task<byte[]?> DownloadAsync(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "maildownload/" + id));
        using var response = await _http.SendAsync(request, ct);
        var final = response.RequestMessage?.RequestUri;
        if (final != null && IsLoginPage(final))
            throw new ConnectorException("The Baader portal ended the session while downloading documents; the next sync signs in again.");
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Baader document download failed: HTTP {Status}; it is retried at the next sync.", (int)response.StatusCode);
            return null;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
        {
            _log.LogWarning("Baader document download did not return a PDF; it is retried at the next sync.");
            return null;
        }
        return bytes;
    }

    /// <summary>The signed-in customers' ids, or null when there is no valid session.</summary>
    private async Task<List<string>?> CustomersAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "fstatus"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, ct);
        var final = response.RequestMessage?.RequestUri;
        if ((int)response.StatusCode is >= 300 and < 400 or 401 or 403 || (final != null && IsLoginPage(final))) return null;
        if (!response.IsSuccessStatusCode)
            throw new ConnectorException($"The Baader portal answered HTTP {(int)response.StatusCode}.");
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try { json = JsonElement.Parse(text); }
        catch (JsonException) { return null; } // an HTML page: the sign-in
        if (json.ValueKind != JsonValueKind.Array) return null;
        var ids = new List<string>();
        foreach (var c in json.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("customer_id", out var id)) continue;
            var value = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString();
            if (!string.IsNullOrEmpty(value) && CustomerId().IsMatch(value)) ids.Add(value);
        }
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The page token (<c>&lt;meta name="infotok"&gt;</c>) of the signed-in dashboard, sent along with list requests.</summary>
    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(new Uri(_base, "dashboard"), ct);
            var m = InfoToken().Match(html);
            return m.Success ? m.Groups["v"].Value : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var final = response.RequestMessage?.RequestUri;
        if ((int)response.StatusCode is 401 or 403 || (final != null && IsLoginPage(final)))
            throw new ConnectorException("The Baader portal ended the session while listing documents; the next sync signs in again.");
        if (!response.IsSuccessStatusCode)
            throw new ConnectorException($"The Baader portal answered HTTP {(int)response.StatusCode} to the document list.");
        var text = await response.Content.ReadAsStringAsync(ct);
        try { return JsonElement.Parse(text); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The Baader portal's document list was not JSON ({Length} characters).", text.Length);
            throw new ConnectorException("The Baader portal answered the document list in an unexpected form.", ex);
        }
    }

    private bool IsLoginPage(Uri uri) =>
        string.Equals(uri.Host, _base.Host, StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("page=login", StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.None, 1000)]
    private static partial Regex DocumentId();

    [GeneratedRegex("^[0-9A-Za-z]{1,32}$", RegexOptions.None, 1000)]
    private static partial Regex CustomerId();

    [GeneratedRegex("<meta\\s+content=\"(?<v>[^\"]*)\"\\s+name=\"infotok\"", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InfoToken();
}
